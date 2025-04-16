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
            // --- Load Configuration ---
            try
            {
                var configuration = new ConfigurationBuilder()
                   .SetBasePath(AppContext.BaseDirectory)
                   .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                   .Build();
                Settings = configuration.GetSection("AppSettings").Get<AppSettings>();
                if (Settings == null || string.IsNullOrWhiteSpace(Settings.WimlibDownloadUrl) || string.IsNullOrWhiteSpace(Settings.DotNetDownloadPageUrl) || string.IsNullOrWhiteSpace(Settings.DotNetRuntimeInstallerUrl))
                { throw new InvalidOperationException("One or more required settings (URLs) are missing from appsettings.json."); }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Fatal Error: Could not load or validate configuration from appsettings.json."); Console.WriteLine($"Error: {ex.Message}"); Console.ResetColor();
                MessageBox.Show($"Fatal Error loading configuration (appsettings.json):\n\n{ex.Message}\n\nPlease ensure the file exists and is correctly formatted.", "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); return;
            }

            Console.WriteLine("Windows System Imaging Tool");
            Console.WriteLine("---------------------------");

            // --- Admin Check ---
            if (!IsRunningAsAdministrator())
            {
                Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("Error: Administrator privileges required."); Console.ResetColor();
                MessageBox.Show("This tool requires administrator privileges to run.\nPlease restart as Administrator.", "Admin Required", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); return;
            }

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

            string? resolvedDestination = null; // Store final path for backup/restore

            try
            {
                await InitializeRequirements(); // Common requirement check

                if (isBackup)
                {
                    // --- Backup Flow ---
                    resolvedDestination = await GetBackupDestinationPath(args);
                    if (resolvedDestination == null) throw new OperationCanceledException("Backup destination not provided or cancelled.");

                    string? systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(systemDriveLetter) && IsVolumeDirty(systemDriveLetter))
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"\nWarning: Volume {systemDriveLetter} is marked as dirty."); Console.WriteLine("This may indicate filesystem inconsistencies which can cause backup errors."); Console.WriteLine($"It is strongly recommended to run 'chkdsk {systemDriveLetter} /f' and restart before backup."); Console.ResetColor();
                        bool continueBackup = true;
                        if (string.IsNullOrWhiteSpace(destinationArg)) { var userChoice = MessageBox.Show($"Volume {systemDriveLetter} is marked as dirty (potential filesystem issues).\n\nIt is strongly recommended to run 'chkdsk {systemDriveLetter} /f' and restart first.\n\nContinue with backup anyway?", "Filesystem Dirty Bit Set", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2); if (userChoice == DialogResult.No) { continueBackup = false; } } else { Console.WriteLine("Non-interactive mode: Continuing backup despite dirty volume flag."); }
                        if (!continueBackup) { throw new OperationCanceledException("Backup cancelled due to dirty volume flag."); }
                    }

                    string? destinationDriveLetter = Path.GetPathRoot(resolvedDestination)?.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(systemDriveLetter) && !string.IsNullOrEmpty(destinationDriveLetter)) { InitializePerformanceCounters(systemDriveLetter, destinationDriveLetter); } else { Console.WriteLine("Warning: Could not determine source or destination drive letter for performance monitoring."); }

                    await PerformSystemBackup(resolvedDestination);
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
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\n--- Critical Error ---"); Console.WriteLine($"Message: {ex.Message}"); Console.ResetColor();
                Console.WriteLine("\nStack Trace:"); Console.WriteLine(ex.StackTrace);
                MessageBox.Show($"A critical error occurred:\n\n{ex.Message}\n\nSee console for details.", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                sourceDiskReadCounter?.Dispose();
                destDiskWriteCounter?.Dispose();
                if (!string.IsNullOrWhiteSpace(destinationArg) || !string.IsNullOrWhiteSpace(sourceArg)) { if (Console.CursorTop > 5) { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); } } else { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
            }
        }

        // --- Helper to get Backup Destination Path ---
        private static async Task<string?> GetBackupDestinationPath(string[] args)
        {
            string? destination = args.FirstOrDefault(a => a.StartsWith("-dest=", StringComparison.OrdinalIgnoreCase))
                                       ?.Substring("-dest=".Length).Trim('"');
            if (string.IsNullOrWhiteSpace(destination))
            {
                Console.WriteLine("Preparing backup file selection dialog...");
                destination = await ShowSaveDialogOnStaThreadAsync(); // Use Save dialog
            }
            else
            {
                Console.WriteLine($"Using backup destination from command line: {destination}");
                if (!destination.EndsWith(".wim", StringComparison.OrdinalIgnoreCase)) { destination += ".wim"; Console.WriteLine($"Adjusted backup destination path to: {destination}"); }
            }
            return destination;
        }

        // --- Helper to get Restore Source Path ---
        private static async Task<string?> GetRestoreSourcePath(string? sourceArg)
        {
            string? sourceWim = sourceArg;
            if (string.IsNullOrWhiteSpace(sourceWim))
            {
                Console.WriteLine("Preparing restore file selection dialog...");
                sourceWim = await ShowOpenDialogOnStaThreadAsync(); // Use Open dialog
            }
            else
            {
                Console.WriteLine($"Using restore source from command line: {sourceWim}");
            }
            return sourceWim;
        }

        // --- Requirement Initialization ---
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
            if (!IsDotNetRuntimeInstalled(Settings.DotNetRequiredVersion)) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"Required .NET Runtime not found."); Console.WriteLine($"Please install .NET {Settings.DotNetRequiredVersion} Runtime (or later) manually."); Console.WriteLine($"Download from: {Settings.DotNetDownloadPageUrl}"); Console.ResetColor(); return false; }
            return true;
        }

        private static bool IsDotNetRuntimeInstalled(string requiredVersionString)
        {
            if (!Version.TryParse(requiredVersionString, out var requiredVersion)) { Console.WriteLine($"Warning: Invalid required .NET version format in config: {requiredVersionString}"); return false; }
            try { var psi = new ProcessStartInfo { FileName = "dotnet", Arguments = "--list-runtimes", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 }; using var process = Process.Start(psi); if (process == null) { Console.WriteLine("Warning: Failed to start 'dotnet' process."); return false; } string output = process.StandardOutput.ReadToEnd(); process.WaitForExit(); if (process.ExitCode != 0) { Console.WriteLine("Info: 'dotnet --list-runtimes' command failed or returned non-zero exit code. Assuming .NET is not installed or accessible."); return false; } var runtimeRegex = new Regex(@"^Microsoft\.NETCore\.App\s+(\d+\.\d+\.\d+)", RegexOptions.Multiline); var matches = runtimeRegex.Matches(output); foreach (Match match in matches.Cast<Match>()) { if (match.Groups.Count > 1 && Version.TryParse(match.Groups[1].Value, out var installedVersion)) { if (installedVersion.Major > requiredVersion.Major || (installedVersion.Major == requiredVersion.Major && installedVersion.Minor >= requiredVersion.Minor)) { Console.WriteLine($"Found compatible .NET Runtime: {installedVersion}"); return true; } } } Console.WriteLine("No compatible .NET Runtime version found."); return false; } catch (System.ComponentModel.Win32Exception) { Console.WriteLine("Info: 'dotnet' command not found. Assuming .NET is not installed or not in system PATH."); return false; } catch (Exception ex) { Console.WriteLine($"Warning: Error checking .NET runtime: {ex.Message}"); return false; }
        }

        private static async Task<bool> InstallDotNetRuntime()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"dotnet-runtime-{Settings!.DotNetRequiredVersion}-installer.exe"); Console.WriteLine($"Downloading .NET Runtime installer to: {tempPath}"); try { using var client = new HttpClient(); client.DefaultRequestHeaders.Add("User-Agent", "ImagingTool/1.0"); await using var stream = await client.GetStreamAsync(Settings.DotNetRuntimeInstallerUrl); await using var fileStream = File.Create(tempPath); await stream.CopyToAsync(fileStream); Console.WriteLine("Download complete."); await fileStream.DisposeAsync(); Console.WriteLine("Running .NET Runtime installer (may require elevation)..."); var psi = new ProcessStartInfo { FileName = tempPath, Arguments = "/quiet /norestart", UseShellExecute = true, Verb = "runas" }; using var process = Process.Start(psi); if (process == null) { Console.WriteLine("Error: Failed to start installer process."); return false; } await process.WaitForExitAsync(); if (process.ExitCode == 0 || process.ExitCode == 3010) { Console.WriteLine($".NET Runtime installation completed (Exit Code: {process.ExitCode}). A restart might be needed."); return true; } else { Console.WriteLine($"Error: .NET Runtime installation failed with Exit Code: {process.ExitCode}"); return false; } } catch (HttpRequestException ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Error downloading .NET Runtime: {ex.Message}"); Console.WriteLine($"Please check the URL ({Settings.DotNetRuntimeInstallerUrl}) in appsettings.json and your internet connection."); Console.ResetColor(); return false; } catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Error during .NET Runtime installation: {ex.Message}"); Console.ResetColor(); Console.WriteLine($"Consider installing manually from: {Settings.DotNetDownloadPageUrl}"); return false; } finally { if (File.Exists(tempPath)) { try { File.Delete(tempPath); } catch (IOException ex) { Console.WriteLine($"Warning: Could not delete temp file {tempPath}: {ex.Message}"); } } }
        }

        private static async Task<bool> CheckWimLib()
        {
            Console.WriteLine($"Checking for WimLib at: {WimlibPath}"); if (!File.Exists(WimlibPath)) { Console.WriteLine("WimLib not found. Attempting to download and install..."); return await InstallWimLib(); }
            return true;
        }

        private static async Task<bool> InstallWimLib()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"wimlib-temp-{Guid.NewGuid()}"); var zipPath = Path.Combine(tempDir, "wimlib.zip"); Console.WriteLine($"Downloading WimLib from: {Settings!.WimlibDownloadUrl}"); Console.WriteLine($"Temporary download location: {zipPath}"); try { Directory.CreateDirectory(tempDir); using (var client = new HttpClient()) { client.DefaultRequestHeaders.Add("User-Agent", "ImagingTool/1.0"); Console.WriteLine("Starting download..."); var response = await client.GetAsync(Settings.WimlibDownloadUrl, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync(); await using var fileStream = File.Create(zipPath); await stream.CopyToAsync(fileStream); Console.WriteLine("Download complete."); } Console.WriteLine($"Extracting WimLib archive to: {tempDir}"); ZipFile.ExtractToDirectory(zipPath, tempDir, true); string? foundExePath = Directory.GetFiles(tempDir, Settings.WimlibExeName, SearchOption.AllDirectories).FirstOrDefault(); if (string.IsNullOrEmpty(foundExePath)) { throw new FileNotFoundException($"'{Settings.WimlibExeName}' not found within the downloaded archive from {Settings.WimlibDownloadUrl}. Check WimlibExeName in appsettings.json."); } string? sourceBinDir = Path.GetDirectoryName(foundExePath); if (sourceBinDir == null) { throw new DirectoryNotFoundException("Could not determine the source directory containing wimlib binaries."); } Console.WriteLine($"Copying WimLib binaries from '{sourceBinDir}' to target directory: {WimlibDir}"); Directory.CreateDirectory(WimlibDir); int filesCopied = 0; foreach (var file in Directory.GetFiles(sourceBinDir, "*.*", SearchOption.TopDirectoryOnly)) { string extension = Path.GetExtension(file).ToLowerInvariant(); if (extension == ".exe" || extension == ".dll") { var destPath = Path.Combine(WimlibDir, Path.GetFileName(file)); File.Copy(file, destPath, true); Console.WriteLine($"  Copied: {Path.GetFileName(file)}"); filesCopied++; } } if (filesCopied == 0) { throw new InvalidOperationException($"No .exe or .dll files were found to copy from '{sourceBinDir}'."); } if (!File.Exists(WimlibPath)) { throw new FileNotFoundException($"WimLib installation failed. Expected executable not found at the target path: {WimlibPath}"); } Console.WriteLine("WimLib installation successful."); return true; } catch (HttpRequestException ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Error downloading WimLib: {ex.Message}"); Console.WriteLine($"Please check the URL ({Settings.WimlibDownloadUrl}) in appsettings.json and your internet connection."); Console.ResetColor(); return false; } catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Failed to install WimLib: {ex.Message}"); Console.ResetColor(); if (Directory.Exists(WimlibDir)) { try { Directory.Delete(WimlibDir, true); } catch (IOException ioEx) { Console.WriteLine($"Warning: Could not clean up target WimLib directory '{WimlibDir}': {ioEx.Message}"); } } return false; } finally { if (Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch (IOException ioEx) { Console.WriteLine($"Warning: Could not clean up temp directory '{tempDir}': {ioEx.Message}"); } } }
        }

        // --- Dialog Helpers ---
        private static Task<string?> ShowSaveDialogOnStaThreadAsync()
        {
            var tcs = new TaskCompletionSource<string?>(); var uiThread = new Thread(() => { try { using (var dialog = new SaveFileDialog()) { dialog.Title = "Select Backup Location and Filename"; dialog.Filter = "Windows Image Files (*.wim)|*.wim|All Files (*.*)|*.*"; dialog.DefaultExt = "wim"; dialog.FileName = $"SystemBackup_{DateTime.Now:yyyyMMdd_HHmmss}.wim"; dialog.RestoreDirectory = true; DialogResult result = dialog.ShowDialog(); if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName)) { tcs.TrySetResult(dialog.FileName); } else { tcs.TrySetResult(null); } } } catch (Exception ex) { tcs.TrySetException(ex); } }); uiThread.SetApartmentState(ApartmentState.STA); uiThread.IsBackground = true; uiThread.Start(); return tcs.Task;
        }

        // *** CORRECTED IMPLEMENTATION ***
        private static Task<string?> ShowOpenDialogOnStaThreadAsync()
        {
            var tcs = new TaskCompletionSource<string?>();
            var uiThread = new Thread(() =>
            {
                try
                {
                    using (var dialog = new OpenFileDialog())
                    {
                        dialog.Title = "Select WIM Image File to Restore";
                        dialog.Filter = "Windows Image Files (*.wim)|*.wim|All Files (*.*)|*.*";
                        dialog.DefaultExt = "wim";
                        dialog.CheckFileExists = true; // Make sure the selected file exists
                        dialog.RestoreDirectory = true;

                        // Show the dialog on this dedicated STA thread
                        DialogResult result = dialog.ShowDialog();

                        if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
                        {
                            // Set the selected filename as the result of the Task
                            tcs.TrySetResult(dialog.FileName);
                        }
                        else
                        {
                            // Set null if the user cancelled
                            tcs.TrySetResult(null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // If showing the dialog failed, propagate the exception
                    tcs.TrySetException(ex);
                }
            });
            // Configure and start the dedicated UI thread
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.IsBackground = true;
            uiThread.Start();
            // Return the Task that the calling code can await
            return tcs.Task;
        }
        // *** END OF CORRECTION ***

        // --- Filesystem Dirty Bit Check ---
        private static bool IsVolumeDirty(string driveLetter)
        {
            if (string.IsNullOrEmpty(driveLetter) || !driveLetter.EndsWith(':')) { Console.WriteLine($"Warning: Invalid drive letter format for dirty check: {driveLetter}"); return false; }
            Console.WriteLine($"Checking dirty bit for volume {driveLetter}...");
            var psi = new ProcessStartInfo { FileName = "fsutil.exe", Arguments = $"dirty query {driveLetter}", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            try { using var process = Process.Start(psi); if (process == null) { Console.WriteLine("Warning: Failed to start fsutil process."); return false; } var outputTask = process.StandardOutput.ReadToEndAsync(); if (!process.WaitForExit(5000)) { Console.WriteLine("Warning: fsutil process timed out. Assuming volume is not dirty."); try { process.Kill(); } catch { /* Ignore */ } return false; } string output = outputTask.Result; if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)) { Console.WriteLine($"fsutil output: {output.Trim()}"); if (output.Contains(" is Dirty", StringComparison.OrdinalIgnoreCase)) { return true; } if (output.Contains(" is NOT Dirty", StringComparison.OrdinalIgnoreCase)) { return false; } Console.WriteLine($"Warning: fsutil output format not recognized."); } else { Console.WriteLine($"Warning: fsutil dirty query failed or produced empty output (Exit Code: {process.ExitCode}). Assuming volume is not dirty."); } } catch (Exception ex) { Console.WriteLine($"Warning: Error running fsutil dirty query: {ex.Message}. Assuming volume is not dirty."); }
            return false;
        }

        // --- Initialize Performance Counters ---
        private static void InitializePerformanceCounters(string sourceDrive, string destDrive)
        {
            Console.WriteLine("Initializing performance counters..."); try { const string categoryName = "PhysicalDisk"; const string readCounterName = "Disk Read Bytes/sec"; const string writeCounterName = "Disk Write Bytes/sec"; var category = new PerformanceCounterCategory(categoryName); string[] instanceNames = category.GetInstanceNames(); sourceDiskInstanceName = instanceNames.FirstOrDefault(name => name.Contains($" {sourceDrive.Trim(':')}")); destDiskInstanceName = instanceNames.FirstOrDefault(name => name.Contains($" {destDrive.Trim(':')}")); if (string.IsNullOrEmpty(sourceDiskInstanceName)) sourceDiskInstanceName = instanceNames.FirstOrDefault(name => name.EndsWith(" 0")); if (string.IsNullOrEmpty(destDiskInstanceName)) destDiskInstanceName = instanceNames.FirstOrDefault(name => name.EndsWith(" 1")); if (!string.IsNullOrEmpty(sourceDiskInstanceName)) { sourceDiskReadCounter = new PerformanceCounter(categoryName, readCounterName, sourceDiskInstanceName, readOnly: true); sourceDiskReadCounter.NextValue(); Console.WriteLine($"Monitoring Read Speed for Disk: {sourceDiskInstanceName}"); } else { Console.WriteLine($"Warning: Could not find performance counter instance for source drive {sourceDrive}. Read speed monitoring disabled."); } if (!string.IsNullOrEmpty(destDiskInstanceName)) { destDiskWriteCounter = new PerformanceCounter(categoryName, writeCounterName, destDiskInstanceName, readOnly: true); destDiskWriteCounter.NextValue(); Console.WriteLine($"Monitoring Write Speed for Disk: {destDiskInstanceName}"); } else { Console.WriteLine($"Warning: Could not find performance counter instance for destination drive {destDrive}. Write speed monitoring disabled."); } } catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"Warning: Failed to initialize performance counters. Disk speed monitoring disabled."); Console.WriteLine($"Error: {ex.Message}"); Console.ResetColor(); sourceDiskReadCounter?.Dispose(); sourceDiskReadCounter = null; destDiskWriteCounter?.Dispose(); destDiskWriteCounter = null; }
        }

        // --- Backup Logic ---
        private static async Task PerformSystemBackup(string destination)
        {
            Console.WriteLine("\n--- System Backup ---"); string? destinationDir = Path.GetDirectoryName(destination); if (string.IsNullOrEmpty(destinationDir)) { throw new ArgumentException("Invalid destination path (no directory).", nameof(destination)); }
            Console.WriteLine($"Proceeding with backup to: {destination}"); await CreateSystemImage(destination);
        }

        private static async Task CreateSystemImage(string destination)
        {
            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory); if (string.IsNullOrEmpty(systemDrive)) { throw new InvalidOperationException("Could not determine the system drive root."); }
            Console.WriteLine($"\nStarting backup of system drive '{systemDrive}' to '{destination}'..."); Console.WriteLine("Using Volume Shadow Copy Service (VSS)."); var threads = Environment.ProcessorCount; Console.WriteLine($"Using {threads} threads."); var configFileName = $"wimlib-config-{Guid.NewGuid()}.txt"; var configFilePath = Path.Combine(Path.GetTempPath(), configFileName); Console.WriteLine($"Using temporary config file for exclusions: {configFilePath}"); bool wimlibReportedError = false; string compressionArg; string compressionLevelDisplay; switch (Settings?.WimCompressionLevel?.ToLowerInvariant()) { case "none": compressionArg = "none"; compressionLevelDisplay = "None (Fastest, Largest File)"; break; case "maximum": compressionArg = "lzx"; compressionLevelDisplay = "Maximum (Slowest, Smallest File)"; break; case "fast": default: compressionArg = "fast"; compressionLevelDisplay = "Fast (Balanced)"; break; }
            Console.WriteLine($"Using Compression Level: {compressionLevelDisplay}"); try { var exclusions = new List<string> { "[ExclusionList]", @"\pagefile.sys", @"\swapfile.sys", @"\hiberfil.sys", @"\System Volume Information", @"\RECYCLER", @"\$Recycle.Bin", @"\Windows\Temp\*.*", @"\Windows\Temp", @"\Users\*\AppData\Local\Temp\*.*", @"\Users\*\AppData\Local\Temp", @"\Temp\*.*", @"\Temp", @"\b042787fde8c8f3f_0" }; await File.WriteAllLinesAsync(configFilePath, exclusions); var arguments = $"capture \"{systemDrive.TrimEnd('\\')}\" \"{destination}\" \"Windows System Backup\" \"Backup taken on {DateTime.Now:yyyy-MM-dd HH:mm:ss}\" --snapshot --config=\"{configFilePath}\" --compress={compressionArg} --threads={threads}"; Console.WriteLine($"\nExecuting WimLib command:"); Console.WriteLine($"{WimlibPath} {arguments}\n"); var psi = new ProcessStartInfo { FileName = WimlibPath, Arguments = arguments, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 }; var startTime = DateTime.UtcNow; long totalBytesProcessed = 0L; long lastBytesProcessed = 0L; var lastUpdateTime = startTime; string lastFileName = "Initializing..."; object consoleLock = new object(); using var process = new Process { StartInfo = psi, EnableRaisingEvents = true }; var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); process.OutputDataReceived += (sender, e) => { if (e.Data == null) { outputTcs.TrySetResult(true); return; } }; process.ErrorDataReceived += (sender, e) => { if (e.Data == null) { errorTcs.TrySetResult(true); return; } var errorData = e.Data; bool isError = false; if (errorData.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || errorData.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 || errorData.IndexOf("Cannot", StringComparison.OrdinalIgnoreCase) >= 0) { if (errorData.Contains("Parent inode") && errorData.Contains("was missing from the MFT listing")) { lock (consoleLock) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"\n[WimLib Warning] MFT inconsistency (continuing): {errorData}"); Console.ResetColor(); } } else { lock (consoleLock) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine($"\n[WimLib Error] {errorData}"); Console.ResetColor(); } wimlibReportedError = true; isError = true; } } if (!isError) { try { const string filePrefix = "Adding file: ["; if (errorData.StartsWith(filePrefix) && errorData.EndsWith("]")) { lastFileName = errorData.Substring(filePrefix.Length, errorData.Length - filePrefix.Length - 1); } else if (errorData.Contains("GiB /", StringComparison.OrdinalIgnoreCase) && errorData.Contains("% done", StringComparison.OrdinalIgnoreCase)) { var match = Regex.Match(errorData, @"(\d+(?:[.,]\d+)?)\s*GiB\s*/\s*(\d+(?:[.,]\d+)?)\s*GiB\s*\((\d+)\s*%\s*done\)", RegexOptions.IgnoreCase); if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double processedGiB) && double.TryParse(match.Groups[2].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double totalGiB) && double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double percentage)) { totalBytesProcessed = (long)(processedGiB * 1024 * 1024 * 1024); var now = DateTime.UtcNow; var elapsedTime = now - startTime; var timeSinceLastUpdate = (now - lastUpdateTime).TotalSeconds; double transferSpeedMbps = 0; if (timeSinceLastUpdate > 0.2 && totalBytesProcessed > lastBytesProcessed) { var bytesSinceLastUpdate = totalBytesProcessed - lastBytesProcessed; transferSpeedMbps = (bytesSinceLastUpdate / (1024.0 * 1024.0)) / timeSinceLastUpdate; lastBytesProcessed = totalBytesProcessed; lastUpdateTime = now; } else if (timeSinceLastUpdate > 5) { lastUpdateTime = now; } float readMBps = sourceDiskReadCounter?.NextValue() / (1024f * 1024f) ?? 0f; float writeMBps = destDiskWriteCounter?.NextValue() / (1024f * 1024f) ?? 0f; lock (consoleLock) { string progressLine = $"\r{percentage:F1}% ({processedGiB:F2}/{totalGiB:F2} GiB) | Speed: {transferSpeedMbps:F1} MB/s | Read: {readMBps:F1} MB/s | Write: {writeMBps:F1} MB/s | Elapsed: {elapsedTime:hh\\:mm\\:ss} | File: {Truncate(lastFileName, 50)}"; Console.Write(new string(' ', Console.WindowWidth - 1) + "\r"); Console.Write(progressLine.PadRight(Console.WindowWidth - 1)); } } } } catch (Exception parseEx) { lock (consoleLock) { Console.WriteLine($"\n[Warning] Failed to parse wimlib output line: '{errorData}'. Error: {parseEx.Message}"); } } } }; if (!process.Start()) { throw new InvalidOperationException($"Failed to start WimLib process: {WimlibPath}"); } process.BeginOutputReadLine(); process.BeginErrorReadLine(); await process.WaitForExitAsync(); await Task.WhenAll(outputTcs.Task, errorTcs.Task); lock (consoleLock) { Console.Write(new string(' ', Console.WindowWidth - 1) + "\r"); } Console.WriteLine("\nWimLib process finished."); if (process.ExitCode != 0) { throw new Exception($"WimLib process exited with error code: {process.ExitCode}. Check logs above."); } if (wimlibReportedError) { throw new Exception($"WimLib reported one or more errors during execution (see logs above). Backup may be incomplete or corrupted."); } } catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\n--- Error during backup creation ---"); Console.WriteLine($"Message: {ex.Message}"); Console.ResetColor(); throw; } finally { if (File.Exists(configFilePath)) { try { File.Delete(configFilePath); Console.WriteLine($"Deleted temporary config file: {configFilePath}"); } catch (Exception ex) { Console.WriteLine($"Warning: Could not delete temporary config file '{configFilePath}'. {ex.Message}"); } } }
        }

        // --- Restore Logic ---
        private static async Task PerformSystemRestore(string? sourceArg, string? targetArg)
        {
            Console.WriteLine("\n--- System Restore ---"); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"); Console.WriteLine("!!! WARNING: RESTORING AN IMAGE WILL ERASE ALL DATA ON THE TARGET DRIVE! !!!"); Console.WriteLine("!!!          Ensure you have selected the correct target drive.          !!!"); Console.WriteLine("!!!          THIS OPERATION CANNOT BE UNDONE.                          !!!"); Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"); Console.ResetColor();
            string? sourceWim = await GetRestoreSourcePath(sourceArg); if (string.IsNullOrWhiteSpace(sourceWim) || !File.Exists(sourceWim)) { MessageBox.Show($"Source WIM file not selected or not found: '{sourceWim ?? "null"}'.", "Restore Error", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new OperationCanceledException($"Source WIM file selection cancelled or file not found ('{sourceWim ?? "null"}')."); }
            Console.WriteLine($"Selected restore source: {sourceWim}");
            string? targetDrive = targetArg; if (string.IsNullOrWhiteSpace(targetDrive)) { Console.Write("Enter the TARGET drive letter to restore TO (e.g., D:): "); targetDrive = Console.ReadLine()?.ToUpperInvariant().Trim(); } else { Console.WriteLine($"Using restore target from command line: {targetDrive}"); }
            if (string.IsNullOrEmpty(targetDrive) || !targetDrive.EndsWith(":") || targetDrive.Length != 2 || !Directory.Exists(targetDrive + "\\")) { MessageBox.Show($"Invalid target drive specified: '{targetDrive ?? "null"}'. Must be a valid drive letter (e.g., D:).", "Restore Error", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new OperationCanceledException($"Invalid target drive specified: '{targetDrive ?? "null"}'."); }
            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'); if (targetDrive.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show($"Cannot restore to the currently running Windows drive ({systemDrive}) from within Windows.\nPlease boot into Windows PE or recovery media to restore the active OS partition.", "Restore Error", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new OperationCanceledException($"Cannot restore to active system drive ({systemDrive})."); }
            Console.WriteLine($"Selected restore target: {targetDrive}");
            if (string.IsNullOrWhiteSpace(sourceArg) && string.IsNullOrWhiteSpace(targetArg)) { Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine($"\nARE YOU ABSOLUTELY SURE you want to restore '{sourceWim}'"); Console.WriteLine($"onto drive {targetDrive}? ALL EXISTING DATA ON {targetDrive} WILL BE DESTROYED."); Console.Write("Type 'YES' to continue, or anything else to cancel: "); string? confirmation = Console.ReadLine(); Console.ResetColor(); if (!"YES".Equals(confirmation, StringComparison.Ordinal)) { throw new OperationCanceledException("Restore cancelled by user confirmation."); } } else { Console.WriteLine("Non-interactive mode: Proceeding with restore..."); }
            await ApplyWimImageAndConfigureBoot(sourceWim, targetDrive);
        }

        private static async Task ApplyWimImageAndConfigureBoot(string sourceWim, string targetDrive)
        {
            Console.WriteLine($"\nApplying image '{sourceWim}' to target drive '{targetDrive}'..."); Console.WriteLine("This may take a long time. Monitor progress below."); string targetDirectory = targetDrive + "\\"; int imageIndex = 1; var wimApplyArgs = $"apply \"{sourceWim}\" {imageIndex} \"{targetDirectory}\" --check"; bool wimlibSuccess = await RunProcessAsync(WimlibPath, wimApplyArgs, "WimLib Apply"); if (!wimlibSuccess) { throw new Exception("WimLib apply process failed. Cannot proceed with boot configuration."); }
            Console.WriteLine("WIM image applied successfully.");
            Console.WriteLine("\nConfiguring boot files on target drive..."); Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("Note: This attempts automatic configuration assuming a standard UEFI setup."); Console.WriteLine("Manual configuration using diskpart/bcdboot in WinPE might be needed for complex layouts or BIOS systems."); Console.ResetColor(); string windowsFolderPath = Path.Combine(targetDirectory, "Windows"); var bcdbootArgs = $"\"{windowsFolderPath}\" /f UEFI"; bool bcdbootSuccess = await RunProcessAsync("bcdboot.exe", bcdbootArgs, "BCDBoot"); if (!bcdbootSuccess) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("\n!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"); Console.WriteLine("!!! WARNING: Automatic boot configuration (bcdboot) failed.           !!!"); Console.WriteLine("!!! The files were restored, but the target drive may not be bootable.!!!"); Console.WriteLine("!!! You may need to manually run bcdboot from WinPE/Recovery.         !!!"); Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"); Console.ResetColor(); Console.WriteLine($"Example manual command (run in WinPE, adjust letters): bcdboot {targetDrive}\\Windows /f UEFI"); } else { Console.WriteLine("Boot files configured successfully (attempted)."); }
        }

        // --- Helper to Run External Processes ---
        private static async Task<bool> RunProcessAsync(string fileName, string arguments, string processName)
        {
            Console.WriteLine($"\nExecuting {processName} command:"); Console.WriteLine($"{fileName} {arguments}\n"); var psi = new ProcessStartInfo { FileName = fileName, Arguments = arguments, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }; var processOutput = new StringBuilder(); var processError = new StringBuilder(); object consoleLock = new object(); bool reportedError = false; using var process = new Process { StartInfo = psi, EnableRaisingEvents = true }; var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); process.OutputDataReceived += (sender, e) => { if (e.Data == null) { outputTcs.TrySetResult(true); return; } processOutput.AppendLine(e.Data); lock (consoleLock) { Console.WriteLine($"[{processName} STDOUT] {e.Data}"); } }; process.ErrorDataReceived += (sender, e) => { if (e.Data == null) { errorTcs.TrySetResult(true); return; } processError.AppendLine(e.Data); lock (consoleLock) { Console.WriteLine($"[{processName} STDERR] {e.Data}"); } reportedError = true; }; try { if (!process.Start()) { throw new InvalidOperationException($"Failed to start process: {fileName}"); } process.BeginOutputReadLine(); process.BeginErrorReadLine(); await process.WaitForExitAsync(); await Task.WhenAll(outputTcs.Task, errorTcs.Task); Console.WriteLine($"\n{processName} process finished."); if (process.ExitCode != 0) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"{processName} process exited with error code: {process.ExitCode}."); Console.WriteLine("Error Output:"); Console.WriteLine(processError.ToString()); Console.ResetColor(); return false; } if (reportedError && process.ExitCode == 0) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"{processName} process completed but reported messages on STDERR (see above). Treating as potential issue."); Console.ResetColor(); } Console.WriteLine($"{processName} completed successfully (Exit Code: {process.ExitCode})."); return true; } catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\n--- Error executing {processName} ---"); Console.WriteLine($"Message: {ex.Message}"); Console.ResetColor(); return false; }
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
