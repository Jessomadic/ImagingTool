using System;
using System.Collections.Generic; // For List<T>
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text; // For StringBuilder and Encoding
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms; // Requires <UseWindowsForms>true</UseWindowsForms> in csproj
// Add these for configuration
using Microsoft.Extensions.Configuration;

// PerformanceCounter references removed


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
        public bool IgnoreFileReadErrors { get; set; } = false; // Default to false
    }


    class Program
    {
        // --- Configuration Loading ---
        private static AppSettings? Settings { get; set; }
        private static string WimlibDir => Path.Combine(AppContext.BaseDirectory, Settings?.WimlibSubDir ?? "wimlib");
        private static string WimlibPath => Path.Combine(WimlibDir, Settings?.WimlibExeName ?? "wimlib-imagex.exe");

        // --- Performance Counters REMOVED ---

        // --- Shared lock for logging ---
        private static readonly object logFileLock = new object();


        // --- Main Application Logic ---
        [STAThread]
        static async Task Main(string[] args)
        {
            // ... (Config Loading unchanged) ...
            try { /* Load Settings */ } catch (Exception ex) { /* Handle config error */ return; }

            Console.WriteLine("Windows System Imaging Tool");
            Console.WriteLine("---------------------------");

            string? destinationArg = args.FirstOrDefault(a => a.StartsWith("-dest=", StringComparison.OrdinalIgnoreCase))?.Substring("-dest=".Length).Trim('"');

            if (!IsRunningAsAdministrator()) { /* Handle admin error */ return; }

            string? resolvedDestination = null;
            string? skippedFilesLogPath = null;

            try
            {
                await InitializeRequirements();

                resolvedDestination = await GetDestinationPath(args);
                if (resolvedDestination == null) { throw new OperationCanceledException("Backup destination not provided or cancelled."); }

                skippedFilesLogPath = Path.ChangeExtension(resolvedDestination, ".wim.skipped.log");
                Console.WriteLine($"Skipped files will be logged to: {skippedFilesLogPath}");
                try { if (File.Exists(skippedFilesLogPath)) File.Delete(skippedFilesLogPath); } catch (Exception ex) { Console.WriteLine($"Warning: Could not delete old log file '{skippedFilesLogPath}': {ex.Message}"); }

                string? systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
                if (!string.IsNullOrEmpty(systemDriveLetter) && IsVolumeDirty(systemDriveLetter)) { /* Handle dirty bit */ }

                // --- Call to InitializePerformanceCounters REMOVED ---

                await PerformSystemBackup(resolvedDestination, skippedFilesLogPath);

                Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("\nOperation completed successfully!"); Console.ResetColor();
                if (string.IsNullOrWhiteSpace(destinationArg)) { MessageBox.Show("System backup completed successfully!", "Backup Success", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }
            catch (OperationCanceledException ex) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"\nOperation cancelled: {ex.Message}"); Console.ResetColor(); }
            catch (Exception ex) { /* ... Critical Error Handling unchanged ... */ }
            finally
            {
                // --- Disposal of performance counters REMOVED ---
                // ... (Keep console open logic unchanged) ...
            }
        }

        // --- Helper to get destination path (unchanged) ---
        private static async Task<string?> GetDestinationPath(string[] args) { /* ... unchanged ... */ return ""; }

        // --- Requirement Initialization (unchanged) ---
        private static async Task InitializeRequirements() { /* ... unchanged ... */ }
        private static async Task<bool> CheckDotNetRuntime() { /* ... unchanged ... */ return true; }
        private static bool IsDotNetRuntimeInstalled(string requiredVersionString) { /* ... unchanged ... */ return true; }
        private static async Task<bool> InstallDotNetRuntime() { /* ... unchanged ... */ return true; }
        private static async Task<bool> CheckWimLib() { /* ... unchanged ... */ return true; }
        private static async Task<bool> InstallWimLib() { /* ... unchanged ... */ return true; }

        // --- Helper Method to Show Dialog on Dedicated STA Thread (unchanged) ---
        private static Task<string?> ShowSaveDialogOnStaThreadAsync() { /* ... unchanged ... */ return Task.FromResult<string?>(""); }

        // --- Filesystem Dirty Bit Check (unchanged) ---
        private static bool IsVolumeDirty(string driveLetter) { /* ... unchanged ... */ return false; }

        // --- Initialize Performance Counters method REMOVED ---


        // --- Backup Logic ---
        private static async Task PerformSystemBackup(string destination, string skippedFilesLogPath)
        {
            Console.WriteLine("\n--- System Backup ---");
            string? destinationDir = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(destinationDir)) { throw new ArgumentException("Invalid destination path (no directory).", nameof(destination)); }
            Console.WriteLine($"Proceeding with backup to: {destination}");
            await CreateSystemImage(destination, skippedFilesLogPath);
        }

        private static async Task CreateSystemImage(string destination, string skippedFilesLogPath)
        {
            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory); if (string.IsNullOrEmpty(systemDrive)) { throw new InvalidOperationException("Could not determine the system drive root."); }
            Console.WriteLine($"\nStarting backup of system drive '{systemDrive}' to '{destination}'..."); Console.WriteLine("Using Volume Shadow Copy Service (VSS)."); var threads = Environment.ProcessorCount; Console.WriteLine($"Using {threads} threads."); var configFileName = $"wimlib-config-{Guid.NewGuid()}.txt"; var configFilePath = Path.Combine(Path.GetTempPath(), configFileName); Console.WriteLine($"Using temporary config file for exclusions: {configFilePath}"); bool wimlibReportedError = false; int wimlibExitCode = -1;
            bool showSpinner = true; var spinner = new[] { '|', '/', '-', '\\' }; int spinnerIndex = 0; var cts = new CancellationTokenSource(); Task spinnerTask = Task.CompletedTask;
            bool filesWereSkipped = false;

            string compressionArg; string compressionLevelDisplay;
            switch (Settings?.WimCompressionLevel?.ToLowerInvariant()) { case "none": compressionArg = "none"; compressionLevelDisplay = "None (Fastest, Largest File)"; break; case "maximum": compressionArg = "lzx"; compressionLevelDisplay = "Maximum (Slowest, Smallest File)"; break; case "fast": default: compressionArg = "fast"; compressionLevelDisplay = "Fast (Balanced)"; break; }
            Console.WriteLine($"Using Compression Level: {compressionLevelDisplay}");
            if (Settings?.IgnoreFileReadErrors == true) { Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine("\nWARNING: IgnoreFileReadErrors is enabled. Attempting to skip files with read errors."); Console.WriteLine("         Backup may be INCOMPLETE and UNSTABLE."); Console.ResetColor(); }

            try
            {
                var exclusions = new List<string> { "[ExclusionList]", @"\pagefile.sys", @"\swapfile.sys", @"\hiberfil.sys", @"\System Volume Information", @"\RECYCLER", @"\$Recycle.Bin", @"\Windows\Temp\*.*", @"\Windows\Temp", @"\Users\*\AppData\Local\Temp\*.*", @"\Users\*\AppData\Local\Temp", @"\Temp\*.*", @"\Temp", @"\b042787fde8c8f3f_0" };
                await File.WriteAllLinesAsync(configFilePath, exclusions);

                var arguments = $"capture \"{systemDrive.TrimEnd('\\')}\" \"{destination}\" \"Windows System Backup\" \"Backup taken on {DateTime.Now:yyyy-MM-dd HH:mm:ss}\" --snapshot --config=\"{configFilePath}\" --compress={compressionArg} --threads={threads}";
                Console.WriteLine($"\nExecuting WimLib command:"); Console.WriteLine($"{WimlibPath} {arguments}\n"); var psi = new ProcessStartInfo { FileName = WimlibPath, Arguments = arguments, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 }; var startTime = DateTime.UtcNow; long totalBytesProcessed = 0L; long lastBytesProcessed = 0L; var lastUpdateTime = startTime; string lastFileName = "Initializing..."; object consoleLock = new object(); using var process = new Process { StartInfo = psi, EnableRaisingEvents = true }; var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                spinnerTask = Task.Run(async () => { try { while (!cts.Token.IsCancellationRequested) { lock (consoleLock) { if (showSpinner) { try { Console.SetCursorPosition(0, Console.CursorTop); Console.Write($"Processing... {spinner[spinnerIndex]}"); spinnerIndex = (spinnerIndex + 1) % spinner.Length; } catch { /* Ignore console errors */ } } } await Task.Delay(150, cts.Token); } } catch (OperationCanceledException) { /* Expected on cancel */ } catch (Exception ex) { lock (consoleLock) { Console.WriteLine($"\nSpinner task error: {ex.Message}"); } } }, cts.Token);

                process.OutputDataReceived += (sender, e) => { if (e.Data == null) { outputTcs.TrySetResult(true); return; } };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null) { errorTcs.TrySetResult(true); return; }
                    var errorData = e.Data; bool isError = false; bool isSkippedFileError = false;

                    if (errorData.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || errorData.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 || errorData.IndexOf("Cannot", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isError = true;
                        if (Settings?.IgnoreFileReadErrors == true)
                        {
                            bool isAccessDenied = errorData.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
                            bool isSharingViolation = errorData.Contains("sharing violation", StringComparison.OrdinalIgnoreCase) || errorData.Contains("The process cannot access the file because it is being used by another process", StringComparison.OrdinalIgnoreCase);
                            bool isDeviceNotReady = errorData.Contains("device is not ready", StringComparison.OrdinalIgnoreCase);
                            if (isAccessDenied || isSharingViolation || isDeviceNotReady)
                            {
                                isSkippedFileError = true; isError = false; filesWereSkipped = true;
                                lock (consoleLock) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"\n[Skipping File] Read error (logging to '{Path.GetFileName(skippedFilesLogPath)}'): {errorData}"); Console.ResetColor(); }
                                try { lock (logFileLock) { File.AppendAllText(skippedFilesLogPath, errorData + Environment.NewLine, Encoding.UTF8); } } catch (Exception logEx) { lock (consoleLock) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\n[Error] Failed to write skipped file entry to log '{skippedFilesLogPath}': {logEx.Message}"); Console.ResetColor(); } }
                            }
                        }
                        if (isError && errorData.Contains("Parent inode") && errorData.Contains("was missing from the MFT listing")) { lock (consoleLock) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"\n[WimLib Warning] MFT inconsistency: {errorData}"); Console.ResetColor(); } isError = false; }
                        if (isError) { lock (consoleLock) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine($"\n[WimLib Error] {errorData}"); Console.ResetColor(); } wimlibReportedError = true; }
                    }
                    if (!isError && !isSkippedFileError)
                    {
                        try
                        {
                            const string filePrefix = "Adding file: ["; if (errorData.StartsWith(filePrefix) && errorData.EndsWith("]")) { lastFileName = errorData.Substring(filePrefix.Length, errorData.Length - filePrefix.Length - 1); }
                            else if (errorData.Contains("GiB /", StringComparison.OrdinalIgnoreCase) && errorData.Contains("% done", StringComparison.OrdinalIgnoreCase))
                            {
                                showSpinner = false; var match = Regex.Match(errorData, @"(\d+(?:[.,]\d+)?)\s*GiB\s*/\s*(\d+(?:[.,]\d+)?)\s*GiB\s*\((\d+)\s*%\s*done\)", RegexOptions.IgnoreCase); if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double processedGiB) && double.TryParse(match.Groups[2].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double totalGiB) && double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double percentage))
                                {
                                    totalBytesProcessed = (long)(processedGiB * 1024 * 1024 * 1024); var now = DateTime.UtcNow; var elapsedTime = now - startTime; var timeSinceLastUpdate = (now - lastUpdateTime).TotalSeconds; double transferSpeedMbps = 0; if (timeSinceLastUpdate > 0.2 && totalBytesProcessed > lastBytesProcessed) { var bytesSinceLastUpdate = totalBytesProcessed - lastBytesProcessed; transferSpeedMbps = (bytesSinceLastUpdate / (1024.0 * 1024.0)) / timeSinceLastUpdate; lastBytesProcessed = totalBytesProcessed; lastUpdateTime = now; } else if (timeSinceLastUpdate > 5) { lastUpdateTime = now; }
                                    // --- Performance counter sampling REMOVED ---
                                    lock (consoleLock)
                                    {
                                        // --- Updated Progress Line (Disk speeds REMOVED) ---
                                        string progressLine = $"\r{percentage:F1}% ({processedGiB:F2}/{totalGiB:F2} GiB) | Speed: {transferSpeedMbps:F1} MB/s | Elapsed: {elapsedTime:hh\\:mm\\:ss} | File: {Truncate(lastFileName, 50)}";
                                        try { Console.Write(new string(' ', Console.WindowWidth - 1) + "\r"); Console.Write(progressLine.PadRight(Console.WindowWidth - 1)); } catch { /* Ignore console errors */ }
                                    }
                                }
                            }
                        }
                        catch (Exception parseEx) { lock (consoleLock) { Console.WriteLine($"\n[Warning] Failed to parse wimlib output line: '{errorData}'. Error: {parseEx.Message}"); } }
                    }
                };

                if (!process.Start()) { throw new InvalidOperationException($"Failed to start WimLib process: {WimlibPath}"); }
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                await process.WaitForExitAsync();
                wimlibExitCode = process.ExitCode;
                Console.WriteLine($"\nDEBUG: WimLib process exited with code: {wimlibExitCode}");
                cts.Cancel(); try { await spinnerTask; } catch { /* Ignore */ }
                Console.WriteLine("DEBUG: Waiting for output streams to close..."); /* ... stream timeout ... */ Console.WriteLine("DEBUG: Output streams closed.");
                lock (consoleLock) { try { Console.Write(new string(' ', Console.WindowWidth - 1) + "\r"); } catch { /* Ignore */ } }
                Console.WriteLine("\nWimLib process finished.");

                if (filesWereSkipped) { /* ... Report skipped files and log location ... */ }
                if (wimlibExitCode != 0) { throw new Exception($"WimLib process exited with error code: {wimlibExitCode}. Backup failed. Check logs above."); }
                if (wimlibReportedError) { throw new Exception($"WimLib reported critical errors during execution (see logs above). Backup failed or may be corrupted."); }
            }
            catch (Exception ex) { if (!cts.IsCancellationRequested) cts.Cancel(); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\n--- Error during backup creation ---"); Console.WriteLine($"Message: {ex.Message}"); Console.ResetColor(); throw; }
            finally { if (!spinnerTask.IsCompleted && !spinnerTask.IsCanceled && !spinnerTask.IsFaulted) try { await spinnerTask; } catch { /* Ignore */ } cts.Dispose(); if (File.Exists(configFilePath)) { try { File.Delete(configFilePath); Console.WriteLine($"Deleted temporary config file: {configFilePath}"); } catch (Exception ex) { Console.WriteLine($"Warning: Could not delete temporary config file '{configFilePath}'. {ex.Message}"); } } }
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
    }
}
