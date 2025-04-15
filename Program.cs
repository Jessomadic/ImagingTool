using System;
using System.Collections.Generic; // For List<T>
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms; // Requires <UseWindowsForms>true</UseWindowsForms> in csproj
// Add these for configuration
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
        // --- Configuration Loading ---
        private static AppSettings? Settings { get; set; }
        // Use properties to access derived paths, ensuring Settings is loaded
        private static string WimlibDir => Path.Combine(AppContext.BaseDirectory, Settings?.WimlibSubDir ?? "wimlib");
        private static string WimlibPath => Path.Combine(WimlibDir, Settings?.WimlibExeName ?? "wimlib-imagex.exe");


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

                // Bind the configuration section to the AppSettings object
                Settings = configuration.GetSection("AppSettings").Get<AppSettings>();

                // Validate essential settings
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
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Fatal Error: Could not load or validate configuration from appsettings.json.");
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                // Show message box as console might close
                MessageBox.Show($"Fatal Error loading configuration (appsettings.json):\n\n{ex.Message}\n\nPlease ensure the file exists and is correctly formatted.",
                                "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("Windows System Imaging Tool");
            Console.WriteLine("---------------------------");

            // Check for command-line destination argument (e.g., -dest="C:\Path\To\Backup.wim")
            string? destinationArg = args.FirstOrDefault(a => a.StartsWith("-dest=", StringComparison.OrdinalIgnoreCase))
                                        ?.Substring("-dest=".Length).Trim('"'); // Remove quotes if present


            if (!IsRunningAsAdministrator())
            {
                Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("Error: Administrator privileges required."); Console.ResetColor();
                MessageBox.Show("This tool requires administrator privileges to run.\nPlease restart as Administrator.", "Admin Required", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); return;
            }

            try
            {
                await InitializeRequirements();

                // Check filesystem dirty bit before backup
                string? systemDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
                if (!string.IsNullOrEmpty(systemDriveLetter) && IsVolumeDirty(systemDriveLetter))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\nWarning: Volume {systemDriveLetter} is marked as dirty.");
                    Console.WriteLine("This may indicate filesystem inconsistencies which can cause backup errors.");
                    Console.WriteLine($"It is strongly recommended to run 'chkdsk {systemDriveLetter} /f' and restart before backup.");
                    Console.ResetColor();

                    // Ask user whether to continue only if running interactively (no destination arg)
                    bool continueBackup = true;
                    if (string.IsNullOrWhiteSpace(destinationArg)) // Only show dialog if interactive
                    {
                        var choice = MessageBox.Show($"Volume {systemDriveLetter} is marked as dirty (potential filesystem issues).\n\nIt is strongly recommended to run 'chkdsk {systemDriveLetter} /f' and restart first.\n\nContinue with backup anyway?",
                                                     "Filesystem Dirty Bit Set", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                        if (choice == DialogResult.No)
                        {
                            continueBackup = false;
                        }
                    }
                    else // Running non-interactively
                    {
                        Console.WriteLine("Non-interactive mode: Continuing backup despite dirty volume flag.");
                        // Or you could choose to abort non-interactive runs:
                        // continueBackup = false;
                    }

                    if (!continueBackup)
                    {
                        throw new OperationCanceledException("Backup cancelled due to dirty volume flag.");
                    }
                }

                await PerformSystemBackup(destinationArg); // Pass argument

                Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("\nOperation completed successfully!"); Console.ResetColor();
                // Only show success message box if interactive
                if (string.IsNullOrWhiteSpace(destinationArg))
                {
                    MessageBox.Show("System backup completed successfully!", "Backup Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (OperationCanceledException ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\nOperation cancelled: {ex.Message}"); // Show reason
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\n--- Critical Error ---"); Console.WriteLine($"Message: {ex.Message}"); Console.ResetColor();
                Console.WriteLine("\nStack Trace:"); Console.WriteLine(ex.StackTrace);
                MessageBox.Show($"A critical error occurred:\n\n{ex.Message}\n\nSee console for details.", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // Prevent console closing immediately if run non-interactively with an error
                if (!string.IsNullOrWhiteSpace(destinationArg) && Console.CursorTop > 5) // Basic check if there was output
                {
                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                }
                else if (string.IsNullOrWhiteSpace(destinationArg)) // Always wait if interactive
                {
                    Console.WriteLine("\nPress any key to exit...");
                    Console.ReadKey();
                }
            }
        }

        // --- Requirement Initialization ---
        private static async Task InitializeRequirements()
        {
            Console.WriteLine("\nChecking requirements...");
            // Use Settings object, null check done in Main
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

        // --- Helper Method to Show Dialog on Dedicated STA Thread ---
        private static Task<string?> ShowSaveDialogOnStaThreadAsync()
        {
            var tcs = new TaskCompletionSource<string?>(); var uiThread = new Thread(() => { try { using (var dialog = new SaveFileDialog()) { dialog.Title = "Select Backup Location and Filename"; dialog.Filter = "Windows Image Files (*.wim)|*.wim|All Files (*.*)|*.*"; dialog.DefaultExt = "wim"; dialog.FileName = $"SystemBackup_{DateTime.Now:yyyyMMdd_HHmmss}.wim"; dialog.RestoreDirectory = true; DialogResult result = dialog.ShowDialog(); if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName)) { tcs.TrySetResult(dialog.FileName); } else { tcs.TrySetResult(null); } } } catch (Exception ex) { tcs.TrySetException(ex); } }); uiThread.SetApartmentState(ApartmentState.STA); uiThread.IsBackground = true; uiThread.Start(); return tcs.Task;
        }

        // --- Filesystem Dirty Bit Check ---
        private static bool IsVolumeDirty(string driveLetter)
        {
            if (string.IsNullOrEmpty(driveLetter) || !driveLetter.EndsWith(':'))
            {
                Console.WriteLine($"Warning: Invalid drive letter format for dirty check: {driveLetter}");
                return false; // Cannot check
            }

            Console.WriteLine($"Checking dirty bit for volume {driveLetter}...");
            var psi = new ProcessStartInfo
            {
                FileName = "fsutil.exe",
                Arguments = $"dirty query {driveLetter}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8 // Use UTF8
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null) { Console.WriteLine("Warning: Failed to start fsutil process."); return false; }
                // Read output asynchronously to avoid potential deadlocks with large output
                var outputTask = process.StandardOutput.ReadToEndAsync();
                // Wait for exit with a timeout (e.g., 5 seconds)
                if (!process.WaitForExit(5000))
                {
                    Console.WriteLine("Warning: fsutil process timed out. Assuming volume is not dirty.");
                    try { process.Kill(); } catch { /* Ignore */ }
                    return false;
                }

                string output = outputTask.Result; // Get the captured output

                // Example Output:
                // Volume - C: is NOT Dirty
                // Volume - C: is Dirty
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    Console.WriteLine($"fsutil output: {output.Trim()}");
                    // Be robust about whitespace and case
                    if (output.Contains(" is Dirty", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    if (output.Contains(" is NOT Dirty", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    Console.WriteLine($"Warning: fsutil output format not recognized.");
                }
                else
                {
                    Console.WriteLine($"Warning: fsutil dirty query failed or produced empty output (Exit Code: {process.ExitCode}). Assuming volume is not dirty.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Error running fsutil dirty query: {ex.Message}. Assuming volume is not dirty.");
            }
            return false; // Default to false if check fails
        }


        // --- Backup Logic ---
        private static async Task PerformSystemBackup(string? destinationFromArg)
        {
            Console.WriteLine("\n--- System Backup ---");
            string? destination = destinationFromArg; // Use arg if provided
            string? destinationDir = null;

            // If destination wasn't provided via argument, use the dialog
            if (string.IsNullOrWhiteSpace(destination))
            {
                Console.WriteLine("Preparing file selection dialog...");
                destination = await ShowSaveDialogOnStaThreadAsync();
                if (string.IsNullOrWhiteSpace(destination))
                {
                    // Message already shown by ShowSaveDialogOnStaThreadAsync if error
                    Console.WriteLine("Backup operation cancelled by user.");
                    throw new OperationCanceledException("Backup cancelled via dialog.");
                }
            }
            else
            {
                Console.WriteLine($"Using destination from command line: {destination}");
                // Ensure .wim extension if using argument
                if (!destination.EndsWith(".wim", StringComparison.OrdinalIgnoreCase))
                {
                    destination += ".wim";
                    Console.WriteLine($"Adjusted destination path to: {destination}");
                }
            }

            // --- Proceed with the selected/provided destination ---
            destinationDir = Path.GetDirectoryName(destination);
            Console.WriteLine($"Selected backup destination: {destination}");

            // Directory validation
            if (string.IsNullOrEmpty(destinationDir)) { MessageBox.Show($"Invalid destination path '{destination}'. Must include a directory.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new OperationCanceledException("Invalid destination path (no directory)."); }
            if (!Directory.Exists(destinationDir))
            {
                try { Console.WriteLine($"Destination directory '{destinationDir}' not found. Attempting to create..."); Directory.CreateDirectory(destinationDir); Console.WriteLine("Directory created."); }
                catch (Exception ex) { MessageBox.Show($"Could not create destination directory '{destinationDir}'.\nError: {ex.Message}", "Directory Creation Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new OperationCanceledException($"Cannot create destination directory: {ex.Message}"); }
            }

            // Writability check
            try { var testFileName = $"write_test_{Guid.NewGuid()}.tmp"; var testFilePath = Path.Combine(destinationDir, testFileName); File.WriteAllText(testFilePath, "test"); File.Delete(testFilePath); Console.WriteLine($"Destination directory '{destinationDir}' is writable."); }
            catch (Exception ex) { Console.WriteLine($"Error: Destination directory '{destinationDir}' is not writable. {ex.Message}"); MessageBox.Show($"The destination directory '{destinationDir}' is not writable.\nPlease check permissions.\n\nError: {ex.Message}", "Directory Not Writable", MessageBoxButtons.OK, MessageBoxIcon.Error); throw new OperationCanceledException($"Destination not writable: {ex.Message}"); }

            await CreateSystemImage(destination);
        }

                private static async Task CreateSystemImage(string destination)
        {
            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory); if (string.IsNullOrEmpty(systemDrive)) { throw new InvalidOperationException("Could not determine the system drive root."); }
            Console.WriteLine($"\nStarting backup of system drive '{systemDrive}' to '{destination}'..."); Console.WriteLine("Using Volume Shadow Copy Service (VSS)."); var threads = Environment.ProcessorCount; Console.WriteLine($"Using {threads} threads."); var configFileName = $"wimlib-config-{Guid.NewGuid()}.txt"; var configFilePath = Path.Combine(Path.GetTempPath(), configFileName); Console.WriteLine($"Using temporary config file for exclusions: {configFilePath}"); bool wimlibReportedError = false;

            // --- Determine Compression Argument ---
            string compressionArg;
            string compressionLevelDisplay;
            switch (Settings?.WimCompressionLevel?.ToLowerInvariant())
            {
                case "none":
                    compressionArg = "none";
                    compressionLevelDisplay = "None (Fastest, Largest File)";
                    break;
                case "maximum": // Maps to lzx for best compression
                    compressionArg = "lzx"; // or "maximum"
                    compressionLevelDisplay = "Maximum (Slowest, Smallest File)";
                    break;
                case "fast":
                default: // Default to fast if setting is missing or invalid
                    compressionArg = "fast"; // or "xpress"
                    compressionLevelDisplay = "Fast (Balanced)";
                    break;
            }
            Console.WriteLine($"Using Compression Level: {compressionLevelDisplay}");
            // --- End Compression Argument ---

            try
            {
                var exclusions = new List<string> { "[ExclusionList]", @"\pagefile.sys", @"\swapfile.sys", @"\hiberfil.sys", @"\System Volume Information", @"\RECYCLER", @"\$Recycle.Bin", @"\Windows\Temp\*.*", @"\Windows\Temp", @"\Users\*\AppData\Local\Temp\*.*", @"\Users\*\AppData\Local\Temp", @"\Temp\*.*", @"\Temp", @"\b042787fde8c8f3f_0" };
                await File.WriteAllLinesAsync(configFilePath, exclusions);

                // --- Use the determined compressionArg ---
                var arguments = $"capture \"{systemDrive.TrimEnd('\\')}\" \"{destination}\" \"Windows System Backup\" \"Backup taken on {DateTime.Now:yyyy-MM-dd HH:mm:ss}\" --snapshot --config=\"{configFilePath}\" --compress={compressionArg} --threads={threads}";
                // --- End argument change ---

                Console.WriteLine($"\nExecuting WimLib command:"); Console.WriteLine($"{WimlibPath} {arguments}\n"); var psi = new ProcessStartInfo { FileName = WimlibPath, Arguments = arguments, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 }; var startTime = DateTime.UtcNow; long totalBytesProcessed = 0L; long lastBytesProcessed = 0L; var lastUpdateTime = startTime; string lastFileName = "Initializing..."; object consoleLock = new object(); using var process = new Process { StartInfo = psi, EnableRaisingEvents = true }; var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.OutputDataReceived += (sender, e) => { if (e.Data == null) { outputTcs.TrySetResult(true); return; } };
                process.ErrorDataReceived += (sender, e) => { if (e.Data == null) { errorTcs.TrySetResult(true); return; } var errorData = e.Data; if (errorData.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || errorData.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 || errorData.IndexOf("Cannot", StringComparison.OrdinalIgnoreCase) >= 0) { if (errorData.Contains("Parent inode") && errorData.Contains("was missing from the MFT listing")) { lock (consoleLock) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"\n[WimLib Warning] Encountered known MFT inconsistency (attempting to continue due to exclusion): {errorData}"); Console.ResetColor(); } } else { lock (consoleLock) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine($"\n[WimLib Error] {errorData}"); Console.ResetColor(); } wimlibReportedError = true; } } try { const string filePrefix = "Adding file: ["; if (errorData.StartsWith(filePrefix) && errorData.EndsWith("]")) { lastFileName = errorData.Substring(filePrefix.Length, errorData.Length - filePrefix.Length - 1); } else if (errorData.Contains("GiB /", StringComparison.OrdinalIgnoreCase) && errorData.Contains("% done", StringComparison.OrdinalIgnoreCase)) { var match = Regex.Match(errorData, @"(\d+(?:[.,]\d+)?)\s*GiB\s*/\s*(\d+(?:[.,]\d+)?)\s*GiB\s*\((\d+)\s*%\s*done\)", RegexOptions.IgnoreCase); if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double processedGiB) && double.TryParse(match.Groups[2].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double totalGiB) && double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double percentage)) { totalBytesProcessed = (long)(processedGiB * 1024 * 1024 * 1024); var now = DateTime.UtcNow; var elapsedTime = now - startTime; var timeSinceLastUpdate = (now - lastUpdateTime).TotalSeconds; double transferSpeedMbps = 0; if (timeSinceLastUpdate > 0.2 && totalBytesProcessed > lastBytesProcessed) { var bytesSinceLastUpdate = totalBytesProcessed - lastBytesProcessed; transferSpeedMbps = (bytesSinceLastUpdate / (1024.0 * 1024.0)) / timeSinceLastUpdate; lastBytesProcessed = totalBytesProcessed; lastUpdateTime = now; } else if (timeSinceLastUpdate > 5) { lastUpdateTime = now; } lock (consoleLock) { string progressLine = $"\rProgress: {percentage:F1}% ({processedGiB:F2}/{totalGiB:F2} GiB) | Speed: {transferSpeedMbps:F1} MB/s | Elapsed: {elapsedTime:hh\\:mm\\:ss} | File: {Truncate(lastFileName, 35)}"; Console.Write(new string(' ', Console.WindowWidth - 1) + "\r"); Console.Write(progressLine.PadRight(Console.WindowWidth - 1)); } } } } catch (Exception parseEx) { lock (consoleLock) { Console.WriteLine($"\n[Warning] Failed to parse wimlib output line: '{errorData}'. Error: {parseEx.Message}"); } } };
                if (!process.Start()) { throw new InvalidOperationException($"Failed to start WimLib process: {WimlibPath}"); }
                process.BeginOutputReadLine(); process.BeginErrorReadLine(); await process.WaitForExitAsync(); await Task.WhenAll(outputTcs.Task, errorTcs.Task); lock (consoleLock) { Console.Write(new string(' ', Console.WindowWidth - 1) + "\r"); } Console.WriteLine("\nWimLib process finished.");
                if (process.ExitCode != 0) { throw new Exception($"WimLib process exited with error code: {process.ExitCode}. Check logs above."); }
                if (wimlibReportedError) { throw new Exception($"WimLib reported one or more errors during execution (see logs above). Backup may be incomplete or corrupted."); }
            }
            catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\n--- Error during backup creation ---"); Console.WriteLine($"Message: {ex.Message}"); Console.ResetColor(); throw; }
            finally { if (File.Exists(configFilePath)) { try { File.Delete(configFilePath); Console.WriteLine($"Deleted temporary config file: {configFilePath}"); } catch (Exception ex) { Console.WriteLine($"Warning: Could not delete temporary config file '{configFilePath}'. {ex.Message}"); } } }
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
