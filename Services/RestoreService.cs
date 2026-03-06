using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ImagingTool.Helpers;

namespace ImagingTool.Services
{
    public class RestoreService
    {
        private readonly AppSettings _settings;
        private readonly IProcessRunner _processRunner;

        public RestoreService(AppSettings settings, IProcessRunner processRunner)
        {
            _settings = settings;
            _processRunner = processRunner;
        }

        public async Task PerformSystemRestore(string? sourceArg, string? targetArg)
        {
            Console.WriteLine("\n--- System Restore ---");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Console.WriteLine("!!! WARNING: RESTORING AN IMAGE WILL ERASE ALL DATA ON THE TARGET DRIVE! !!!");
            Console.WriteLine("!!!          Ensure you have selected the correct target drive.          !!!");
            Console.WriteLine("!!!          THIS OPERATION CANNOT BE UNDONE.                          !!!");
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Console.ResetColor();

            string? sourceWim = await GetRestoreSourcePath(sourceArg);
            if (string.IsNullOrWhiteSpace(sourceWim) || !File.Exists(sourceWim))
            {
                MessageBox.Show(
                    $"Source WIM file not selected or not found: '{sourceWim ?? "null"}'.",
                    "Restore Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new OperationCanceledException($"Source WIM file selection cancelled or file not found ('{sourceWim ?? "null"}').");
            }

            Console.WriteLine($"Selected restore source: {sourceWim}");

            string? targetDrive = GetTargetDrive(targetArg);
            if (string.IsNullOrEmpty(targetDrive) || !targetDrive.EndsWith(":") || targetDrive.Length != 2 || !Directory.Exists(targetDrive + "\\"))
            {
                MessageBox.Show(
                    $"Invalid target drive specified: '{targetDrive ?? "null"}'. Must be a valid drive letter (e.g., D:).",
                    "Restore Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new OperationCanceledException($"Invalid target drive specified: '{targetDrive ?? "null"}'.");
            }

            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
            if (targetDrive.Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    $"Cannot restore to the currently running Windows drive ({systemDrive}) from within Windows.\n" +
                    "Please boot into Windows PE or recovery media to restore the active OS partition.",
                    "Restore Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new OperationCanceledException($"Cannot restore to active system drive ({systemDrive}).");
            }

            Console.WriteLine($"Selected restore target: {targetDrive}");

            if (string.IsNullOrWhiteSpace(sourceArg) && string.IsNullOrWhiteSpace(targetArg))
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"\nARE YOU ABSOLUTELY SURE you want to restore '{sourceWim}'");
                Console.WriteLine($"onto drive {targetDrive}? ALL EXISTING DATA ON {targetDrive} WILL BE DESTROYED.");
                Console.Write("Type 'YES' to continue, or anything else to cancel: ");
                string? confirmation = Console.ReadLine();
                Console.ResetColor();

                if (!"YES".Equals(confirmation, StringComparison.Ordinal))
                    throw new OperationCanceledException("Restore cancelled by user confirmation.");
            }
            else
            {
                Console.WriteLine("Non-interactive mode: Proceeding with restore...");
            }

            await ApplyWimImageAndConfigureBoot(sourceWim, targetDrive);
        }

        private static async Task<string?> GetRestoreSourcePath(string? sourceArg)
        {
            if (!string.IsNullOrWhiteSpace(sourceArg))
            {
                Console.WriteLine($"Using restore source from command line: {sourceArg}");
                return sourceArg;
            }

            Console.WriteLine("Preparing restore file selection dialog...");
            return await DialogHelper.ShowOpenDialogOnStaThreadAsync();
        }

        private static string? GetTargetDrive(string? targetArg)
        {
            if (!string.IsNullOrWhiteSpace(targetArg))
            {
                Console.WriteLine($"Using restore target from command line: {targetArg}");
                return targetArg;
            }

            Console.Write("Enter the TARGET drive letter to restore TO (e.g., D:): ");
            return Console.ReadLine()?.ToUpperInvariant().Trim();
        }

        internal async Task ApplyWimImageAndConfigureBoot(string sourceWim, string targetDrive)
        {
            Console.WriteLine($"\nApplying image '{sourceWim}' to target drive '{targetDrive}'...");
            Console.WriteLine("This may take a long time. Monitor progress below.");

            string targetDirectory = targetDrive + "\\";
            var wimApplyArgs = $"apply \"{sourceWim}\" 1 \"{targetDirectory}\" --check";

            object consoleLock = new object();

            void onLine(string line)
            {
                if (line.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))
                {
                    lock (consoleLock)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\n[WimLib Warning] {line}");
                        Console.ResetColor();
                    }
                    return;
                }

                var match = Regex.Match(line,
                    @"(\d+(?:[.,]\d+)?)\s*GiB\s*/\s*(\d+(?:[.,]\d+)?)\s*GiB\s*\((\d+)\s*%\s*done\)",
                    RegexOptions.IgnoreCase);

                if (!match.Success) return;
                if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double processedGiB)) return;
                if (!double.TryParse(match.Groups[2].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double totalGiB)) return;
                if (!double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double percentage)) return;

                double remainingGiB = totalGiB - processedGiB;
                lock (consoleLock)
                {
                    string progressLine =
                        $"\r{percentage:F1}% | {processedGiB:F2}/{totalGiB:F2} GiB | Left: {remainingGiB:F2} GiB";
                    Console.Write(new string(' ', VolumeHelper.SafeConsoleWidth()) + "\r");
                    Console.Write(progressLine.PadRight(VolumeHelper.SafeConsoleWidth()));
                }
            }

            bool wimlibSuccess = await _processRunner.RunProcessWithProgressAsync(
                _settings.WimlibPath, wimApplyArgs, "WimLib Apply", onLine);

            Console.Write(new string(' ', VolumeHelper.SafeConsoleWidth()) + "\r");

            if (!wimlibSuccess)
                throw new Exception("WimLib apply process failed. Cannot proceed with boot configuration.");

            Console.WriteLine("WIM image applied successfully.");
            Console.WriteLine("\nConfiguring boot files on target drive...");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Note: Configuring boot for both UEFI and BIOS firmware (/f ALL).");
            Console.ResetColor();

            string windowsFolderPath = Path.Combine(targetDirectory, "Windows");
            var bcdbootArgs = $"\"{windowsFolderPath}\" /f ALL";
            bool bcdbootSuccess = await _processRunner.RunProcessAsync("bcdboot.exe", bcdbootArgs, "BCDBoot");

            if (!bcdbootSuccess)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                Console.WriteLine("!!! WARNING: Automatic boot configuration (bcdboot) failed.           !!!");
                Console.WriteLine("!!! The files were restored, but the target drive may not be bootable.!!!");
                Console.WriteLine("!!! You may need to manually run bcdboot from WinPE/Recovery.         !!!");
                Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                Console.ResetColor();
                Console.WriteLine($"Example manual command (run in WinPE, adjust letters): bcdboot {targetDrive}\\Windows /f UEFI");
            }
            else
            {
                Console.WriteLine("Boot files configured successfully (attempted).");
            }
        }
    }
}
