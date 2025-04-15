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

// Required for PerformanceCounter - ensure System.Diagnostics.PerformanceCounter NuGet if needed (usually included)


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

        // --- Performance Counters ---
        private static PerformanceCounter? sourceDiskReadCounter;
        private static PerformanceCounter? destDiskWriteCounter;
        private static string? sourceDiskInstanceName;
        private static string? destDiskInstanceName;

        // --- Shared lock for logging ---
        private static readonly object logFileLock = new object();


        // --- Main Application Logic ---
        [STAThread]
        static async Task Main(string[] args)
        {
            // Load Configuration First
            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                Settings = configuration.GetSection("AppSettings").Get<AppSettings>();

                if (Settings == null ||
                    string.IsNullOrWhiteSpace(Settings.WimlibDownloadUrl) ||
                    string.IsNullOrWhiteSpace(Settings.DotNetDownloadPageUrl) ||
                    string.IsNullOrWhiteSpace(Settings.DotNetRuntimeInstallerUrl))
                {
                    throw new InvalidOperationException("One or more required settings (URLs) are missing from appsettings.json.");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Fatal Error: Could not load or validate configuration from appsettings.json."); Console.WriteLine($"Error: {ex.Message}"); Console.ResetColor();
                MessageBox.Show($"Fatal Error loading configuration (appsettings.json):\n\n{ex.Message}\n\nPlease ensure the file exists and is correctly formatted.", "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); return;
            }

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

                // Initialize performance counters using resolved destination
                string? destinationDriveLetter = Path.GetPathRoot(resolvedDestination)?.TrimEnd('\\');
                if (!string.IsNullOrEmpty(systemDriveLetter) && !string.IsNullOrEmpty(destinationDriveLetter))
                {
                    InitializePerformanceCounters(systemDriveLetter, destinationDriveLetter);
                }
                else { Console.WriteLine("Warning: Could not determine source or destination drive letter for performance monitoring."); }

                await PerformSystemBackup(resolvedDestination, skippedFilesLogPath);

                Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("\nOperation completed successfully!"); Console.ResetColor();
                if (string.IsNullOrWhiteSpace(destinationArg)) { MessageBox.Show("System backup completed successfully!", "Backup Success", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }
            catch (OperationCanceledException ex) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"\nOperation cancelled: {ex.Message}"); Console.ResetColor(); }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\n--- Critical Error ---"); Console.WriteLine($"Message: {ex.Message}"); Console.ResetColor();
                Console.WriteLine("\nStack Trace:"); Console.WriteLine(ex.StackTrace);
                MessageBox.Show($"A critical error occurred:\n\n{ex.Message}\n\nSee console for details.", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // Dispose performance counters
                sourceDiskReadCounter?.Dispose();
                destDiskWriteCounter?.Dispose();
                // Keep console open logic
                if (!string.IsNullOrWhiteSpace(destinationArg) && Console.CursorTop > 5) { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); } else if (string.IsNullOrWhiteSpace(destinationArg)) { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
            }
        }

        // --- Helper to get destination path (unchanged) ---
        private static async Task<string?> GetDestinationPath(string[] args)
        {
            string? destination = args.FirstOrDefault(a => a.StartsWith("-dest=", StringComparison.OrdinalIgnoreCase))
                                       ?.Substring("-dest=".Length).Trim('"');
            if (string.IsNullOrWhiteSpace(destination))
            {
                Console.WriteLine("Preparing file selection dialog...");
                destination = await ShowSaveDialogOnStaThreadAsync();
                if (string.IsNullOrWhiteSpace(destination))
                {
                    Console.WriteLine("Backup operation cancelled by user or dialog failed.");
                    return null;
                }
            }
            else
            {
                Console.WriteLine($"Using destination from command line: {destination}");
                if (!destination.EndsWith(".wim", StringComparison.OrdinalIgnoreCase))
                {
                    destination += ".wim";
                    Console.WriteLine($"Adjusted destination path to: {destination}");
                }
            }
            return destination;
        }

        // --- Requirement Initialization (unchanged) ---
        private static async Task InitializeRequirements()
        {
            Console.WriteLine("\nChecking requirements...");
            if (!await CheckDotNetRuntime()) { MessageBox.Show($"Failed requirement: .NET {Settings!.DotNetRequiredVersion} Runtime is missing or incompatible.\nPlease install it manually from:\n{Settings.DotNetDownloadPageUrl}", "Requirement Error", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new InvalidOperationException($"Failed requirement: .NET {Settings.DotNetRequiredVersion} Runtime."); } else { Console.WriteLine($".NET {Settings.DotNetRequiredVersion} Runtime check passed."); }
            if (!await CheckWimLib()) { MessageBox.Show($"Failed requirement: WimLib was not found or could not be installed.\nPlease ensure '{WimlibPath}' exists or try installing manually from {Settings.WimlibDownloadUrl}", "Requirement Error", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new InvalidOperationException($"Failed requirement: WimLib installation."); } else { Console.WriteLine("WimLib check passed."); }
            Console.WriteLine("Requirements met.");
        }

        private static async Task<bool> CheckDotNetRuntime()
        {
            Console.WriteLine($"Checking for .NET Runtime {Settings!.DotNetRequiredVersion} or later...");
            if (!IsDotNetRuntimeInstalled(Settings.DotNetRequiredVersion))
            {
                Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"Required .NET Runtime not found."); Console.WriteLine($"Please install .NET {Settings.DotNetRequiredVersion} Runtime (or later) manually."); Console.WriteLine($"Download from: {Settings.DotNetDownloadPageUrl}"); Console.ResetColor(); return false;
            }
            return true;
        }

        private static bool IsDotNetRuntimeInstalled(string requiredVersionString)
        {
            if (!Version.TryParse(requiredVersionString, out var requiredVersion)) { Console.WriteLine($"Warning: Invalid required .NET version format in config: {requiredVersionString}"); return false; }
            try
            {
                var psi = new ProcessStartInfo { FileName = "dotnet", Arguments = "--list-runtimes", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
                using var process = Process.Start(psi); if (process == null) { Console.WriteLine("Warning: Failed to start 'dotnet' process."); return false; }
                string output = process.StandardOutput.ReadToEnd(); process.WaitForExit(); if (process.ExitCode != 0) { Console.WriteLine("Info: 'dotnet --list-runtimes' command failed or returned non-zero exit code. Assuming .NET is not installed or accessible."); return false; }
                var runtimeRegex = new Regex(@"^Microsoft\.NETCore\.App\s+(\d+\.\d+\.\d+)", RegexOptions.Multiline); var matches = runtimeRegex.Matches(output);
                foreach (Match match in matches.Cast<Match>()) { if (match.Groups.Count > 1 && Version.TryParse(match.Groups[1].Value, out var installedVersion)) { if (installedVersion.Major > requiredVersion.Major || (installedVersion.Major == requiredVersion.Major && installedVersion.Minor >= requiredVersion.Minor)) { Console.WriteLine($"Found compatible .NET Runtime: {installedVersion}"); return true; } } }
                Console.WriteLine("No compatible .NET Runtime version found."); return false;
            }
            catch (System.ComponentModel.Win32Exception) { Console.WriteLine("Info: 'dotnet' command not found. Assuming .NET is not installed or not in system PATH."); return false; }
            catch (Exception ex) { Console.WriteLine($"Warning: Error checking .NET runtime: {ex.Message}"); return false; }
        }

        private static async Task<bool> InstallDotNetRuntime() // Uses config URLs
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"dotnet-runtime-{Settings!.DotNetRequiredVersion}-installer.exe"); Console.WriteLine($"Downloading .NET Runtime installer to: {tempPath}");
            try
            {
                using var client = new HttpClient(); client.DefaultRequestHeaders.Add("User-Agent", "ImagingTool/1.0"); await using var stream = await client.GetStreamAsync(Settings.DotNetRuntimeInstallerUrl); await using var fileStream = File.Create(tempPath); await stream.CopyToAsync(fileStream); Console.WriteLine("Download complete."); await fileStream.DisposeAsync();
                Console.WriteLine("Running .NET Runtime installer (may require elevation)..."); var psi = new ProcessStartInfo { FileName = tempPath, Arguments = "/quiet /norestart", UseShellExecute = true, Verb = "runas" }; using var process = Process.Start(psi); if (process == null) { Console.WriteLine("Error: Failed to start installer process."); return false; }
                await process.WaitForExitAsync();
                if (process.ExitCode == 0 || process.ExitCode == 3010) { Console.WriteLine($".NET Runtime installation completed (Exit Code: {process.ExitCode}). A restart might be needed."); return true; } else { Console.WriteLine($"Error: .NET Runtime installation failed with Exit Code: {process.ExitCode}"); return false; }
            }
            catch (HttpRequestException ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Error downloading .NET Runtime: {ex.Message}"); Console.WriteLine($"Please check the URL ({Settings.DotNetRuntimeInstallerUrl}) in appsettings.json and your internet connection."); Console.ResetColor(); return false; }
            catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Error during .NET Runtime installation: {ex.Message}"); Console.ResetColor(); Console.WriteLine($"Consider installing manually from: {Settings.DotNetDownloadPageUrl}"); return false; }
            finally { if (File.Exists(tempPath)) { try { File.Delete(tempPath); } catch (IOException ex) { Console.WriteLine($"Warning: Could not delete temp file {tempPath}: {ex.Message}"); } } }
        }

        private static async Task<bool> CheckWimLib() // Uses config paths
        {
            Console.WriteLine($"Checking for WimLib at: {WimlibPath}");
            if (!File.Exists(WimlibPath)) { Console.WriteLine("WimLib not found. Attempting to download and install..."); return await InstallWimLib(); }
            return true;
        }

        private static async Task<bool> InstallWimLib() // Uses config URL and paths
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"wimlib-temp-{Guid.NewGuid()}"); var zipPath = Path.Combine(tempDir, "wimlib.zip"); Console.WriteLine($"Downloading WimLib from: {Settings!.WimlibDownloadUrl}"); Console.WriteLine($"Temporary download location: {zipPath}");
            try
            {
                Directory.CreateDirectory(tempDir); using (var client = new HttpClient()) { client.DefaultRequestHeaders.Add("User-Agent", "ImagingTool/1.0"); Console.WriteLine("Starting download..."); var response = await client.GetAsync(Settings.WimlibDownloadUrl, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync(); await using var fileStream = File.Create(zipPath); await stream.CopyToAsync(fileStream); Console.WriteLine("Download complete."); }
                Console.WriteLine($"Extracting WimLib archive to: {tempDir}"); ZipFile.ExtractToDirectory(zipPath, tempDir, true);
                string? foundExePath = Directory.GetFiles(tempDir, Settings.WimlibExeName, SearchOption.AllDirectories).FirstOrDefault(); if (string.IsNullOrEmpty(foundExePath)) { throw new FileNotFoundException($"'{Settings.WimlibExeName}' not found within the downloaded archive from {Settings.WimlibDownloadUrl}. Check WimlibExeName in appsettings.json."); }
                string? sourceBinDir = Path.GetDirectoryName(foundExePath); if (sourceBinDir == null) { throw new DirectoryNotFoundException("Could not determine the source directory containing wimlib binaries."); }
                Console.WriteLine($"Copying WimLib binaries from '{sourceBinDir}' to target directory: {WimlibDir}"); Directory.CreateDirectory(WimlibDir); int filesCopied = 0; foreach (var file in Directory.GetFiles(sourceBinDir, "*.*", SearchOption.TopDirectoryOnly)) { string extension = Path.GetExtension(file).ToLowerInvariant(); if (extension == ".exe" || extension == ".dll") { var destPath = Path.Combine(WimlibDir, Path.GetFileName(file)); File.Copy(file, destPath, true); Console.WriteLine($"  Copied: {Path.GetFileName(file)}"); filesCopied++; } }
                if (filesCopied == 0) { throw new InvalidOperationException($"No .exe or .dll files were found to copy from '{sourceBinDir}'."); }
                if (!File.Exists(WimlibPath)) { throw new FileNotFoundException($"WimLib installation failed. Expected executable not found at the target path: {WimlibPath}"); }
                Console.WriteLine("WimLib installation successful."); return true;
            }
            catch (HttpRequestException ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Error downloading WimLib: {ex.Message}"); Console.WriteLine($"Please check the URL ({Settings.WimlibDownloadUrl}) in appsettings.json and your internet connection."); Console.ResetColor(); return false; }
            catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Failed to install WimLib: {ex.Message}"); Console.ResetColor(); if (Directory.Exists(WimlibDir)) { try { Directory.Delete(WimlibDir, true); } catch (IOException ioEx) { Console.WriteLine($"Warning: Could not clean up target WimLib directory '{WimlibDir}': {ioEx.Message}"); } } return false; }
            finally { if (Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch (IOException ioEx) { Console.WriteLine($"Warning: Could not clean up temp directory '{tempDir}': {ioEx.Message}"); } } }
        }

        // --- Helper Method to Show Dialog on Dedicated STA Thread (unchanged) ---
        private static Task<string?> ShowSaveDialogOnStaThreadAsync()
        {
            var tcs = new TaskCompletionSource<string?>(); var uiThread = new Thread(() => { try { using (var dialog = new SaveFileDialog()) { dialog.Title = "Select Backup Location and Filename"; dialog.Filter = "Windows Image Files (*.wim)|*.wim|All Files (*.*)|*.*"; dialog.DefaultExt = "wim"; dialog.FileName = $"SystemBackup_{DateTime.Now:yyyyMMdd_HHmmss}.wim"; dialog.RestoreDirectory = true; DialogResult result = dialog.ShowDialog(); if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName)) { tcs.TrySetResult(dialog.FileName); } else { tcs.TrySetResult(null); } } } catch (Exception ex) { tcs.TrySetException(ex); } }); uiThread.SetApartmentState(ApartmentState.STA); uiThread.IsBackground = true; uiThread.Start(); return tcs.Task;
        }

        // --- Filesystem Dirty Bit Check (unchanged) ---
        private static bool IsVolumeDirty(string driveLetter)
        {
            if (string.IsNullOrEmpty(driveLetter) || !driveLetter.EndsWith(':')) { Console.WriteLine($"Warning: Invalid drive letter format for dirty check: {driveLetter}"); return false; }
            Console.WriteLine($"Checking dirty bit for volume {driveLetter}...");
            var psi = new ProcessStartInfo { FileName = "fsutil.exe", Arguments = $"dirty query {driveLetter}", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            try
            {
                using var process = Process.Start(psi); if (process == null) { Console.WriteLine("Warning: Failed to start fsutil process."); return false; }
                var outputTask = process.StandardOutput.ReadToEndAsync(); if (!process.WaitForExit(5000)) { Console.WriteLine("Warning: fsutil process timed out. Assuming volume is not dirty."); try { process.Kill(); } catch { /* Ignore */ } return false; }
                string output = outputTask.Result;
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)) { Console.WriteLine($"fsutil output: {output.Trim()}"); if (output.Contains(" is Dirty", StringComparison.OrdinalIgnoreCase)) { return true; } if (output.Contains(" is NOT Dirty", StringComparison.OrdinalIgnoreCase)) { return false; } Console.WriteLine($"Warning: fsutil output format not recognized."); } else { Console.WriteLine($"Warning: fsutil dirty query failed or produced empty output (Exit Code: {process.ExitCode}). Assuming volume is not dirty."); }
            }
            catch (Exception ex) { Console.WriteLine($"Warning: Error running fsutil dirty query: {ex.Message}. Assuming volume is not dirty."); }
            return false;
        }

        // --- Initialize Performance Counters (Includes improved logging from before) ---
        private static void InitializePerformanceCounters(string sourceDrive, string destDrive)
        {
            Console.WriteLine("\nInitializing performance counters...");
            sourceDiskReadCounter?.Dispose(); sourceDiskReadCounter = null; destDiskWriteCounter?.Dispose(); destDiskWriteCounter = null; sourceDiskInstanceName = null; destDiskInstanceName = null;
            try
            {
                const string categoryName = "PhysicalDisk"; const string readCounterName = "Disk Read Bytes/sec"; const string writeCounterName = "Disk Write Bytes/sec";
                if (!PerformanceCounterCategory.Exists(categoryName)) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"ERROR: Performance counter category '{categoryName}' does not exist."); Console.ResetColor(); return; }
                var category = new PerformanceCounterCategory(categoryName); string[] instanceNames = category.GetInstanceNames();
                Console.WriteLine($"Available '{categoryName}' instances: [ {string.Join(" | ", instanceNames)} ]");

                sourceDiskInstanceName = instanceNames.FirstOrDefault(name => name.Contains($" {sourceDrive.Trim(':')}"));
                destDiskInstanceName = instanceNames.FirstOrDefault(name => name.Contains($" {destDrive.Trim(':')}"));

                if (string.IsNullOrEmpty(sourceDiskInstanceName)) { Console.WriteLine($"Info: Could not find instance name containing ' {sourceDrive.Trim(':')}'. Trying fallback match for ' 0'..."); sourceDiskInstanceName = instanceNames.FirstOrDefault(name => Regex.IsMatch(name.Trim(), @"^\d+\s*") && name.Trim().StartsWith("0")); }
                if (string.IsNullOrEmpty(destDiskInstanceName)) { Console.WriteLine($"Info: Could not find instance name containing ' {destDrive.Trim(':')}'. Trying fallback match..."); int sourceNum = -1; if (!string.IsNullOrEmpty(sourceDiskInstanceName) && int.TryParse(sourceDiskInstanceName.Split(' ')[0], out sourceNum)) { destDiskInstanceName = instanceNames.FirstOrDefault(name => Regex.IsMatch(name.Trim(), @"^\d+\s*") && name.Trim().StartsWith((sourceNum + 1).ToString())); } if (string.IsNullOrEmpty(destDiskInstanceName)) { destDiskInstanceName = instanceNames.FirstOrDefault(name => Regex.IsMatch(name.Trim(), @"^\d+\s*") && name.Trim().StartsWith("1")); } }

                if (!string.IsNullOrEmpty(sourceDiskInstanceName)) { try { sourceDiskReadCounter = new PerformanceCounter(categoryName, readCounterName, sourceDiskInstanceName, readOnly: true); sourceDiskReadCounter.NextValue(); Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"OK: Monitoring Read Speed for Disk Instance: '{sourceDiskInstanceName}'"); Console.ResetColor(); } catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"ERROR: Failed to create Read counter for instance '{sourceDiskInstanceName}'. {ex.Message}"); Console.ResetColor(); sourceDiskInstanceName = null; } } else { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"ERROR: Could not automatically determine performance counter instance for source drive {sourceDrive}. Read speed monitoring disabled."); Console.ResetColor(); }
                if (!string.IsNullOrEmpty(destDiskInstanceName)) { try { destDiskWriteCounter = new PerformanceCounter(categoryName, writeCounterName, destDiskInstanceName, readOnly: true); destDiskWriteCounter.NextValue(); Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"OK: Monitoring Write Speed for Disk Instance: '{destDiskInstanceName}'"); Console.ResetColor(); } catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"ERROR: Failed to create Write counter for instance '{destDiskInstanceName}'. {ex.Message}"); Console.ResetColor(); destDiskInstanceName = null; } } else { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"ERROR: Could not automatically determine performance counter instance for destination drive {destDrive}. Write speed monitoring disabled."); Console.ResetColor(); }
            }
            catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"ERROR: Failed to initialize performance counters category. Disk speed monitoring disabled."); Console.WriteLine($"Error: {ex.Message}"); Console.ResetColor(); sourceDiskReadCounter?.Dispose(); sourceDiskReadCounter = null; destDiskWriteCounter?.Dispose(); destDiskWriteCounter = null; }
            Console.WriteLine("Performance counter initialization finished.");
        }


        // --- Backup Logic ---
        private static async Task PerformSystemBackup(string destination, string skippedFilesLogPath)
        {
            Console.WriteLine("\n--- System Backup ---");
            string? destinationDir = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(destinationDir)) { throw new ArgumentException("Invalid destination path (no directory).", nameof(destination)); }
            Console.WriteLine($"Proceeding with backup to: {destination}");
            await CreateSystemImage(destination, skippedFilesLogPath);
        }

        // CreateSystemImage with spinner AND performance counter sampling/display AND skipped file logging
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
                                    // --- Sample Performance Counters ---
                                    float readMBps = 0f; float writeMBps = 0f;
                                    try { readMBps = sourceDiskReadCounter?.NextValue() / (1024f * 1024f) ?? 0f; writeMBps = destDiskWriteCounter?.NextValue() / (1024f * 1024f) ?? 0f; }
                                    catch (InvalidOperationException perfEx) { lock (consoleLock) { Console.WriteLine($"\n[Warning] Performance counter error: {perfEx.Message}"); } sourceDiskReadCounter?.Dispose(); sourceDiskReadCounter = null; destDiskWriteCounter?.Dispose(); destDiskWriteCounter = null; }
                                    // --- End Sample ---
                                    lock (consoleLock)
                                    {
                                        // --- Updated Progress Line (Includes Read/Write) ---
                                        string progressLine = $"\r{percentage:F1}% ({processedGiB:F2}/{totalGiB:F2} GiB) | Speed: {transferSpeedMbps:F1} MB/s | Read: {readMBps:F1} MB/s | Write: {writeMBps:F1} MB/s | Elapsed: {elapsedTime:hh\\:mm\\:ss} | File: {Truncate(lastFileName, 50)}";
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
