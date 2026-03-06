using System.Diagnostics;
using System.Windows.Forms;
using ImagingTool.Helpers;
using ImagingTool.Services;
using Microsoft.Extensions.Configuration;

namespace ImagingTool
{
    class Program
    {
        [STAThread]
        static async Task Main(string[] args)
        {
            AppSettings settings;

            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                settings = configuration.GetSection("AppSettings").Get<AppSettings>()
                    ?? throw new InvalidOperationException("AppSettings section is missing from appsettings.json.");

                if (string.IsNullOrWhiteSpace(settings.WimlibDownloadUrl) ||
                    string.IsNullOrWhiteSpace(settings.DotNetDownloadPageUrl) ||
                    string.IsNullOrWhiteSpace(settings.DotNetRuntimeInstallerUrl))
                {
                    throw new InvalidOperationException("One or more required URL settings are missing from appsettings.json.");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Fatal Error: Could not load or validate configuration from appsettings.json.");
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                MessageBox.Show(
                    $"Fatal Error loading configuration (appsettings.json):\n\n{ex.Message}\n\n" +
                    "Please ensure the file exists and is correctly formatted.",
                    "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("Windows System Imaging Tool");
            Console.WriteLine("---------------------------");

            if (!VolumeHelper.IsRunningAsAdministrator())
            {
                Console.WriteLine("Not running as administrator. Requesting elevation...");
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName,
                        Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
                        Verb = "runas",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // User clicked No on the UAC prompt.
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: Administrator privileges are required to run this tool.");
                    Console.ResetColor();
                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                }
                return;
            }

            Console.WriteLine("\nPlease select an operation:");
            Console.WriteLine("  1. Backup System Drive");
            Console.WriteLine("  2. Restore System Image");
            Console.WriteLine("  3. Verify WIM Image");
            Console.WriteLine("  4. Extract to Existing Install");
            Console.Write("Enter choice (1, 2, 3, or 4): ");
            string? choice = Console.ReadLine();

            string? destinationArg = args
                .FirstOrDefault(a => a.StartsWith("-dest=", StringComparison.OrdinalIgnoreCase))
                ?.Substring("-dest=".Length).Trim('"');
            string? sourceArg = args
                .FirstOrDefault(a => a.StartsWith("-source=", StringComparison.OrdinalIgnoreCase))
                ?.Substring("-source=".Length).Trim('"');
            string? targetArg = args
                .FirstOrDefault(a => a.StartsWith("-target=", StringComparison.OrdinalIgnoreCase))
                ?.Substring("-target=".Length).Trim('"');
            string? verifyArg = args
                .FirstOrDefault(a => a.StartsWith("-verify=", StringComparison.OrdinalIgnoreCase))
                ?.Substring("-verify=".Length).Trim('"');
            string? extractSrcArg = args
                .FirstOrDefault(a => a.StartsWith("-extractsrc=", StringComparison.OrdinalIgnoreCase))
                ?.Substring("-extractsrc=".Length).Trim('"');
            string? extractDstArg = args
                .FirstOrDefault(a => a.StartsWith("-extractdst=", StringComparison.OrdinalIgnoreCase))
                ?.Substring("-extractdst=".Length).Trim('"');

            bool isBackup = choice == "1" || (!string.IsNullOrWhiteSpace(destinationArg) && string.IsNullOrWhiteSpace(sourceArg));
            bool isRestore = choice == "2" || (!string.IsNullOrWhiteSpace(sourceArg) && !string.IsNullOrWhiteSpace(targetArg));
            bool isVerify = choice == "3" || !string.IsNullOrWhiteSpace(verifyArg);
            bool isExtract = choice == "4" || !string.IsNullOrWhiteSpace(extractSrcArg);

            var processRunner = new ProcessRunner();
            var requirements = new RequirementsService(settings);
            var backupService = new BackupService(settings);
            var restoreService = new RestoreService(settings, processRunner);
            var verifyService = new VerifyService(settings, processRunner);
            var extractService = new ExtractService(settings, processRunner);

            try
            {
                await requirements.InitializeRequirements();

                if (isBackup)
                {
                    string? destination = await GetBackupDestinationPath(destinationArg);
                    if (destination == null)
                        throw new OperationCanceledException("Backup destination not provided or cancelled.");

                    string? systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(systemDriveLetter) && VolumeHelper.IsVolumeDirty(systemDriveLetter))
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\nWarning: Volume {systemDriveLetter} is marked as dirty.");
                        Console.WriteLine("This may indicate filesystem inconsistencies which can cause backup errors.");
                        Console.WriteLine($"It is strongly recommended to run 'chkdsk {systemDriveLetter} /f' and restart before backup.");
                        Console.ResetColor();

                        bool continueBackup = true;
                        if (string.IsNullOrWhiteSpace(destinationArg))
                        {
                            var userChoice = MessageBox.Show(
                                $"Volume {systemDriveLetter} is marked as dirty (potential filesystem issues).\n\n" +
                                $"It is strongly recommended to run 'chkdsk {systemDriveLetter} /f' and restart first.\n\n" +
                                "Continue with backup anyway?",
                                "Filesystem Dirty Bit Set", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                            continueBackup = userChoice == DialogResult.Yes;
                        }
                        else
                        {
                            Console.WriteLine("Non-interactive mode: Continuing backup despite dirty volume flag.");
                        }

                        if (!continueBackup)
                            throw new OperationCanceledException("Backup cancelled due to dirty volume flag.");
                    }

                    string? destinationDriveLetter = Path.GetPathRoot(destination)?.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(systemDriveLetter) && !string.IsNullOrEmpty(destinationDriveLetter))
                        backupService.InitializePerformanceCounters(systemDriveLetter, destinationDriveLetter);
                    else
                        Console.WriteLine("Warning: Could not determine source or destination drive letter for performance monitoring.");

                    await backupService.PerformSystemBackup(destination);

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("\nBackup operation completed successfully!");
                    Console.ResetColor();

                    if (string.IsNullOrWhiteSpace(destinationArg))
                        MessageBox.Show("System backup completed successfully!", "Backup Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (isRestore)
                {
                    await restoreService.PerformSystemRestore(sourceArg, targetArg);

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("\nRestore operation completed successfully!");
                    Console.ResetColor();

                    if (string.IsNullOrWhiteSpace(sourceArg) && string.IsNullOrWhiteSpace(targetArg))
                        MessageBox.Show(
                            "System restore completed successfully!\nNote: Boot files were configured automatically (experimental).",
                            "Restore Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (isVerify)
                {
                    string? wimPath = verifyArg;
                    if (string.IsNullOrWhiteSpace(wimPath))
                    {
                        Console.WriteLine("Preparing file selection dialog...");
                        wimPath = await DialogHelper.ShowOpenDialogOnStaThreadAsync();
                    }

                    if (string.IsNullOrWhiteSpace(wimPath))
                        throw new OperationCanceledException("No WIM file selected for verification.");

                    await verifyService.VerifyWimFile(wimPath);

                    if (string.IsNullOrWhiteSpace(verifyArg))
                        MessageBox.Show("WIM verification passed. Image integrity confirmed.", "Verify Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (isExtract)
                {
                    string? extractSrc = extractSrcArg;
                    if (string.IsNullOrWhiteSpace(extractSrc))
                    {
                        Console.WriteLine("Select the WIM file to extract from...");
                        extractSrc = await DialogHelper.ShowOpenDialogOnStaThreadAsync();
                    }

                    if (string.IsNullOrWhiteSpace(extractSrc))
                        throw new OperationCanceledException("No WIM file selected.");

                    if (!File.Exists(extractSrc))
                        throw new FileNotFoundException($"WIM file not found: {extractSrc}");

                    string? extractDst = extractDstArg;
                    if (string.IsNullOrWhiteSpace(extractDst))
                    {
                        Console.Write("Enter target root drive (e.g. C:\\): ");
                        extractDst = Console.ReadLine()?.Trim().Trim('"');
                    }

                    if (string.IsNullOrWhiteSpace(extractDst) || !Directory.Exists(extractDst))
                        throw new ArgumentException($"Target drive not found or not specified: {extractDst}");

                    await extractService.PerformExtraction(extractSrc, extractDst);

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("\nExtract operation completed.");
                    Console.ResetColor();

                    if (string.IsNullOrWhiteSpace(extractSrcArg))
                        MessageBox.Show(
                            "Extraction complete!\n\nPrograms, user data, and app registry have been merged.\n" +
                            "Log out and back in for per-user settings (HKCU) to take effect.",
                            "Extract Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    Console.WriteLine("Invalid choice.");
                }
            }
            catch (OperationCanceledException ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\nOperation cancelled: {ex.Message}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n--- Critical Error ---");
                Console.WriteLine($"Message: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine("\nStack Trace:");
                Console.WriteLine(ex.StackTrace);
                MessageBox.Show(
                    $"A critical error occurred:\n\n{ex.Message}\n\nSee console for details.",
                    "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                bool nonInteractive = !string.IsNullOrWhiteSpace(destinationArg) ||
                                      !string.IsNullOrWhiteSpace(sourceArg) ||
                                      !string.IsNullOrWhiteSpace(verifyArg) ||
                                      !string.IsNullOrWhiteSpace(extractSrcArg);
                if (!nonInteractive || Console.CursorTop <= 5)
                {
                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                }
            }
        }

        private static async Task<string?> GetBackupDestinationPath(string? destinationArg)
        {
            if (!string.IsNullOrWhiteSpace(destinationArg))
            {
                string dest = destinationArg;
                if (!dest.EndsWith(".wim", StringComparison.OrdinalIgnoreCase))
                {
                    dest += ".wim";
                    Console.WriteLine($"Adjusted backup destination path to: {dest}");
                }
                Console.WriteLine($"Using backup destination from command line: {dest}");
                return dest;
            }

            Console.WriteLine("Preparing backup file selection dialog...");
            return await DialogHelper.ShowSaveDialogOnStaThreadAsync();
        }
    }
}
