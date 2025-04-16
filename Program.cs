using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text; // Added for StringBuilder
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;


namespace ImagingTool
{
    // --- Configuration Class ---
    public class AppSettings
    {
        public string WimlibSubDir { get; set; } = "wimlib";
        public string WimlibExeName { get; set; } = "wimlib-imagex.exe";
        public string WimlibDownloadUrl { get; set; } = ""; // Loaded from JSON
        public string DotNetRequiredVersion { get; set; } = "9.0"; // Default if not in JSON
        public string DotNetDownloadPageUrl { get; set; } = ""; // Loaded from JSON
        public string DotNetRuntimeInstallerUrl { get; set; } = ""; // Loaded from JSON
        public string WimCompressionLevel { get; set; } = "Fast"; // Default to Fast if not in JSON
    }


    class Program
    {
        // --- Configuration & Counters ---
        private static AppSettings? Settings { get; set; }
        private static string WimlibDir => Path.Combine(AppContext.BaseDirectory, Settings?.WimlibSubDir ?? "wimlib");
        private static string WimlibPath => Path.Combine(WimlibDir, Settings?.WimlibExeName ?? "wimlib-imagex.exe");
        private static PerformanceCounter? sourceDiskReadCounter;
        private static PerformanceCounter? destDiskWriteCounter;
        private static string? sourceDiskInstanceName;
        private static string? destDiskInstanceName;

        // --- Main Application Logic ---
        [STAThread]
        static async Task Main(string[] args)
        {
            // --- Load Configuration (Unchanged) ---
            try { /* ... config loading ... */ } catch (Exception ex) { /* ... config error handling ... */ return; }

            Console.WriteLine("Windows System Imaging Tool");
            Console.WriteLine("---------------------------");

            // --- Admin Check (Unchanged) ---
            if (!IsRunningAsAdministrator()) { /* ... admin error handling ... */ return; }

            // --- Operation Choice ---
            Console.WriteLine("\nPlease select an operation:");
            Console.WriteLine("  1. Backup System Drive");
            Console.WriteLine("  2. Restore System Image");
            Console.Write("Enter choice (1 or 2): ");
            string? choice = Console.ReadLine();

            string? destinationArg = args.FirstOrDefault(a => a.StartsWith("-dest=", StringComparison.OrdinalIgnoreCase))?.Substring("-dest=".Length).Trim('"');
            string? sourceArg = args.FirstOrDefault(a => a.StartsWith("-source=", StringComparison.OrdinalIgnoreCase))?.Substring("-source=".Length).Trim('"');
            string? targetArg = args.FirstOrDefault(a => a.StartsWith("-target=", StringComparison.OrdinalIgnoreCase))?.Substring("-target=".Length).Trim('"');

            bool isBackup = choice == "1";
            bool isRestore = choice == "2";
            if (!string.IsNullOrWhiteSpace(destinationArg) && string.IsNullOrWhiteSpace(sourceArg)) isBackup = true;
            if (!string.IsNullOrWhiteSpace(sourceArg) && !string.IsNullOrWhiteSpace(targetArg)) isRestore = true;

            try
            {
                await InitializeRequirements(); // Common requirement check

                if (isBackup)
                {
                    // --- Backup Flow (Unchanged from previous version) ---
                    string? backupDestination = await GetBackupDestinationPath(args);
                    if (backupDestination == null) throw new OperationCanceledException("Backup destination not provided or cancelled.");
                    string? systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(systemDriveLetter) && IsVolumeDirty(systemDriveLetter)) { /* ... Dirty bit check ... */ }
                    string? destinationDriveLetter = Path.GetPathRoot(backupDestination)?.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(systemDriveLetter) && !string.IsNullOrEmpty(destinationDriveLetter)) { InitializePerformanceCounters(systemDriveLetter, destinationDriveLetter); } else { Console.WriteLine("Warning: Could not determine source or destination drive letter for performance monitoring."); }
                    await PerformSystemBackup(backupDestination);
                    Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("\nBackup operation completed successfully!"); Console.ResetColor();
                    if (string.IsNullOrWhiteSpace(destinationArg)) { MessageBox.Show("System backup completed successfully!", "Backup Success", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                }
                else if (isRestore)
                {
                    // --- Restore Flow ---
                    await PerformSystemRestore(sourceArg, targetArg); // Pass args
                    Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("\nRestore operation completed successfully!"); Console.ResetColor();
                    if (string.IsNullOrWhiteSpace(sourceArg) && string.IsNullOrWhiteSpace(targetArg)) { MessageBox.Show("System restore completed successfully!\nNote: Boot files were configured automatically (experimental).", "Restore Success", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                }
                else { Console.WriteLine("Invalid choice."); }
            }
            catch (OperationCanceledException ex) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"\nOperation cancelled: {ex.Message}"); Console.ResetColor(); }
            catch (Exception ex) { /* ... Critical Error Handling (Unchanged) ... */ }
            finally { /* ... Dispose Counters & Keep Console Open Logic (Unchanged) ... */ }
        }

        // --- Helper to get Backup Destination Path (Unchanged) ---
        private static async Task<string?> GetBackupDestinationPath(string[] args) { /* ... unchanged ... */ return "dummy_save.wim"; }
        // --- Helper to get Restore Source Path (Unchanged) ---
        private static async Task<string?> GetRestoreSourcePath(string? sourceArg) { /* ... unchanged ... */ return "dummy_open.wim"; }
        // --- Requirement Initialization (Unchanged) ---
        private static async Task InitializeRequirements() { /* ... unchanged ... */ }
        private static async Task<bool> CheckDotNetRuntime() { /* ... unchanged ... */ return true; }
        private static bool IsDotNetRuntimeInstalled(string v) { /* ... unchanged ... */ return true; }
        private static async Task<bool> InstallDotNetRuntime() { /* ... unchanged ... */ return true; }
        private static async Task<bool> CheckWimLib() { /* ... unchanged ... */ return true; }
        private static async Task<bool> InstallWimLib() { /* ... unchanged ... */ return true; }
        // --- Dialog Helpers (Unchanged) ---
        private static Task<string?> ShowSaveDialogOnStaThreadAsync() { /* ... unchanged ... */ return Task.FromResult<string?>("dummy_save.wim"); }
        private static Task<string?> ShowOpenDialogOnStaThreadAsync() { /* ... unchanged ... */ return Task.FromResult<string?>("dummy_open.wim"); }
        // --- Filesystem Dirty Bit Check (Unchanged) ---
        private static bool IsVolumeDirty(string d) { /* ... unchanged ... */ return false; }
        // --- Initialize Performance Counters (Unchanged) ---
        private static void InitializePerformanceCounters(string s, string d) { /* ... unchanged ... */ }
        // --- Backup Logic (Unchanged) ---
        private static async Task PerformSystemBackup(string destination) { /* ... unchanged ... */ }
        private static async Task CreateSystemImage(string destination) { /* ... unchanged ... */ }

        // --- Restore Logic ---

        private static async Task PerformSystemRestore(string? sourceArg, string? targetArg)
        {
            Console.WriteLine("\n--- System Restore ---");
            Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"); Console.WriteLine("!!! WARNING: RESTORING AN IMAGE WILL ERASE ALL DATA ON THE TARGET DRIVE! !!!"); Console.WriteLine("!!!          Ensure you have selected the correct target drive.          !!!"); Console.WriteLine("!!!          THIS OPERATION CANNOT BE UNDONE.                          !!!"); Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"); Console.ResetColor();

            // 1. Get Source WIM File
            string? sourceWim = await GetRestoreSourcePath(sourceArg);
            if (string.IsNullOrWhiteSpace(sourceWim) || !File.Exists(sourceWim)) { MessageBox.Show($"Source WIM file not selected or not found: '{sourceWim ?? "null"}'.", "Restore Error", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new OperationCanceledException($"Source WIM file selection cancelled or file not found ('{sourceWim ?? "null"}')."); }
            Console.WriteLine($"Selected restore source: {sourceWim}");

            // 2. Get Target Drive
            string? targetDrive = targetArg;
            if (string.IsNullOrWhiteSpace(targetDrive)) { Console.Write("Enter the TARGET drive letter to restore TO (e.g., D:): "); targetDrive = Console.ReadLine()?.ToUpperInvariant().Trim(); } else { Console.WriteLine($"Using restore target from command line: {targetDrive}"); }
            if (string.IsNullOrEmpty(targetDrive) || !targetDrive.EndsWith(":") || targetDrive.Length != 2 || !Directory.Exists(targetDrive + "\\")) { MessageBox.Show($"Invalid target drive specified: '{targetDrive ?? "null"}'. Must be a valid drive letter (e.g., D:).", "Restore Error", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new OperationCanceledException($"Invalid target drive specified: '{targetDrive ?? "null"}'."); }
            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
            if (targetDrive.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show($"Cannot restore to the currently running Windows drive ({systemDrive}) from within Windows.\nPlease boot into Windows PE or recovery media to restore the active OS partition.", "Restore Error", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new OperationCanceledException($"Cannot restore to active system drive ({systemDrive})."); }
            Console.WriteLine($"Selected restore target: {targetDrive}");

            // 3. Final Confirmation (only if interactive)
            if (string.IsNullOrWhiteSpace(sourceArg) && string.IsNullOrWhiteSpace(targetArg))
            {
                Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine($"\nARE YOU ABSOLUTELY SURE you want to restore '{sourceWim}'"); Console.WriteLine($"onto drive {targetDrive}? ALL EXISTING DATA ON {targetDrive} WILL BE DESTROYED."); Console.Write("Type 'YES' to continue, or anything else to cancel: "); string? confirmation = Console.ReadLine(); Console.ResetColor();
                if (!"YES".Equals(confirmation, StringComparison.Ordinal)) { throw new OperationCanceledException("Restore cancelled by user confirmation."); }
            }
            else { Console.WriteLine("Non-interactive mode: Proceeding with restore..."); }

            // 4. Execute Restore (Apply WIM and Configure Boot)
            await ApplyWimImageAndConfigureBoot(sourceWim, targetDrive);
        }

        private static async Task ApplyWimImageAndConfigureBoot(string sourceWim, string targetDrive)
        {
            // --- Step 1: Apply the WIM image ---
            Console.WriteLine($"\nApplying image '{sourceWim}' to target drive '{targetDrive}'...");
            Console.WriteLine("This may take a long time. Monitor progress below.");
            string targetDirectory = targetDrive + "\\";
            int imageIndex = 1; // Assuming first image
            var wimApplyArgs = $"apply \"{sourceWim}\" {imageIndex} \"{targetDirectory}\" --check";
            bool wimlibSuccess = await RunProcessAsync(WimlibPath, wimApplyArgs, "WimLib Apply");

            if (!wimlibSuccess)
            {
                throw new Exception("WimLib apply process failed. Cannot proceed with boot configuration.");
            }
            Console.WriteLine("WIM image applied successfully.");

            // --- Step 2: Configure Boot Files using bcdboot ---
            Console.WriteLine("\nConfiguring boot files on target drive...");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Note: This attempts automatic configuration assuming a standard UEFI setup.");
            Console.WriteLine("Manual configuration using diskpart/bcdboot in WinPE might be needed for complex layouts or BIOS systems.");
            Console.ResetColor();

            // Construct bcdboot arguments
            // Target the Windows folder on the newly restored drive
            // Use /f UEFI primarily, could use /f ALL to try both BIOS and UEFI
            string windowsFolderPath = Path.Combine(targetDirectory, "Windows");
            var bcdbootArgs = $"\"{windowsFolderPath}\" /f UEFI"; // Target UEFI primarily
            // Alternative: var bcdbootArgs = $"\"{windowsFolderPath}\" /f ALL"; // Try both

            bool bcdbootSuccess = await RunProcessAsync("bcdboot.exe", bcdbootArgs, "BCDBoot");

            if (!bcdbootSuccess)
            {
                // Don't throw an exception here, as the file restore succeeded.
                // Just warn the user they need manual steps.
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                Console.WriteLine("!!! WARNING: Automatic boot configuration (bcdboot) failed.           !!!");
                Console.WriteLine("!!! The files were restored, but the target drive may not be bootable.!!!");
                Console.WriteLine("!!! You may need to manually run bcdboot from WinPE/Recovery.         !!!");
                Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                Console.ResetColor();
                // Optionally, provide example manual command:
                Console.WriteLine($"Example manual command (run in WinPE, adjust letters): bcdboot {targetDrive}\\Windows /f UEFI");
            }
            else
            {
                Console.WriteLine("Boot files configured successfully (attempted).");
            }
        }

        // --- Helper to Run External Processes ---
        private static async Task<bool> RunProcessAsync(string fileName, string arguments, string processName)
        {
            Console.WriteLine($"\nExecuting {processName} command:");
            Console.WriteLine($"{fileName} {arguments}\n");
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, // Use UTF8 for broader compatibility
                StandardErrorEncoding = Encoding.UTF8
            };

            var processOutput = new StringBuilder();
            var processError = new StringBuilder();
            object consoleLock = new object(); // Separate lock if needed, or reuse main one
            bool reportedError = false;

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (sender, e) => {
                if (e.Data == null) { outputTcs.TrySetResult(true); return; }
                processOutput.AppendLine(e.Data);
                // Optionally print stdout from helper processes
                lock (consoleLock) { Console.WriteLine($"[{processName} STDOUT] {e.Data}"); }
            };
            process.ErrorDataReceived += (sender, e) => {
                if (e.Data == null) { errorTcs.TrySetResult(true); return; }
                processError.AppendLine(e.Data);
                // Always print stderr from helper processes
                lock (consoleLock) { Console.WriteLine($"[{processName} STDERR] {e.Data}"); }
                // Consider any stderr output an error for simplicity, especially for bcdboot/diskpart
                reportedError = true;
            };

            try
            {
                if (!process.Start()) { throw new InvalidOperationException($"Failed to start process: {fileName}"); }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync();
                await Task.WhenAll(outputTcs.Task, errorTcs.Task); // Ensure streams are closed

                Console.WriteLine($"\n{processName} process finished.");

                if (process.ExitCode != 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"{processName} process exited with error code: {process.ExitCode}.");
                    Console.WriteLine("Error Output:");
                    Console.WriteLine(processError.ToString()); // Show captured stderr
                    Console.ResetColor();
                    return false; // Indicate failure
                }
                if (reportedError && process.ExitCode == 0) // Sometimes tools write warnings to stderr but exit 0
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"{processName} process completed but reported messages on STDERR (see above). Treating as potential issue.");
                    Console.ResetColor();
                    // Decide if stderr warnings should count as failure
                    // return false; // Uncomment to treat stderr warnings as failure
                }

                Console.WriteLine($"{processName} completed successfully (Exit Code: {process.ExitCode}).");
                return true; // Indicate success
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n--- Error executing {processName} ---");
                Console.WriteLine($"Message: {ex.Message}");
                Console.ResetColor();
                return false; // Indicate failure
            }
        }


        // --- Utility Functions ---
        private static bool IsRunningAsAdministrator()
        {
            try { using var identity = WindowsIdentity.GetCurrent(); var principal = new WindowsPrincipal(identity); return principal.IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception ex) { Console.WriteLine($"Warning: Could not determine administrator status: {ex.Message}"); return false; }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty; if (value.Length <= maxLength) return value; return "..." + value.Substring(value.Length - maxLength + 3);
        }
    } // End Program Class
} // End Namespace
