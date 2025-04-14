using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text.RegularExpressions;
// Add this for the new UI thread
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ImagingTool
{
    class Program
    {
        // --- Configuration Section ---
        private static readonly string WimlibDir = Path.Combine(AppContext.BaseDirectory, "wimlib");
        private static readonly string WimlibPath = Path.Combine(WimlibDir, "wimlib-imagex.exe");
        private const string WimlibUrl = "https://wimlib.net/downloads/wimlib-1.14.4-windows-x86_64-bin.zip";
        private const string DotNetVersionString = "9.0";
        private const string DotNetDownloadPageUrl = "https://dotnet.microsoft.com/en-us/download/dotnet/9.0";
        private const string DotNetRuntimeInstallerUrl = "https://download.visualstudio.microsoft.com/download/pr/f9ea5363-4c04-4e59-91e6-a71c56b89e0b/1e515a1c6a1a1a1a1a1a1a1a1a1a1a1a/dotnet-runtime-9.0.0-win-x64.exe";

        // --- Main Application Logic ---

        [STAThread] // Main thread
        static async Task Main(string[] args)
        {
            Console.WriteLine("Windows System Imaging Tool");
            Console.WriteLine("---------------------------");

            if (!IsRunningAsAdministrator())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: This tool requires administrator privileges to run.");
                Console.ResetColor();
                MessageBox.Show("This tool requires administrator privileges to run.\nPlease restart as Administrator.", "Administrator Required", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Console.WriteLine("Please restart the application as an administrator.");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            try
            {
                // InitializeRequirements runs on the main STA thread
                await InitializeRequirements();

                // PerformSystemBackup will now handle showing the dialog on a separate thread
                await PerformSystemBackup();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nOperation completed successfully!");
                Console.ResetColor();
                MessageBox.Show("System backup completed successfully!", "Backup Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nOperation cancelled by user.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n--- Critical Error ---");
                Console.WriteLine($"Message: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine("\nStack Trace:");
                Console.WriteLine(ex.StackTrace);
                MessageBox.Show($"A critical error occurred:\n\n{ex.Message}\n\nSee console for details.", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            }
        }

        // --- Requirement Initialization ---
        private static async Task InitializeRequirements()
        {
            Console.WriteLine("\nChecking requirements...");
            if (!await CheckDotNetRuntime())
            {
                MessageBox.Show($"Failed requirement: .NET {DotNetVersionString} Runtime is missing or incompatible.\nPlease install it manually from:\n{DotNetDownloadPageUrl}",
                                "Requirement Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new InvalidOperationException($"Failed requirement: .NET {DotNetVersionString} Runtime.");
            }
            else
            {
                Console.WriteLine($".NET {DotNetVersionString} Runtime check passed.");
            }

            if (!await CheckWimLib())
            {
                MessageBox.Show($"Failed requirement: WimLib was not found or could not be installed.\nPlease ensure '{WimlibPath}' exists or try installing manually.",
                                "Requirement Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new InvalidOperationException($"Failed requirement: WimLib installation.");
            }
            else
            {
                Console.WriteLine("WimLib check passed.");
            }
            Console.WriteLine("Requirements met.");
        }

        private static async Task<bool> CheckDotNetRuntime()
        {
            Console.WriteLine($"Checking for .NET Runtime {DotNetVersionString} or later...");
            if (!IsDotNetRuntimeInstalled(DotNetVersionString))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Required .NET Runtime not found.");
                Console.WriteLine($"Please install .NET {DotNetVersionString} Runtime (or later) manually.");
                Console.WriteLine($"Download from: {DotNetDownloadPageUrl}");
                Console.ResetColor();
                // NOTE: Automatic installation disabled by default
                // return await InstallDotNetRuntime();
                return false;
            }
            return true;
        }

        private static bool IsDotNetRuntimeInstalled(string requiredVersionString)
        {
            if (!Version.TryParse(requiredVersionString, out var requiredVersion)) { Console.WriteLine($"Warning: Invalid required .NET version format in code: {requiredVersionString}"); return false; }
            try
            {
                var psi = new ProcessStartInfo { FileName = "dotnet", Arguments = "--list-runtimes", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
                using var process = Process.Start(psi);
                if (process == null) { Console.WriteLine("Warning: Failed to start 'dotnet' process."); return false; }
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) { Console.WriteLine("Info: 'dotnet --list-runtimes' command failed or returned non-zero exit code. Assuming .NET is not installed or accessible."); return false; }
                var runtimeRegex = new Regex(@"^Microsoft\.NETCore\.App\s+(\d+\.\d+\.\d+)", RegexOptions.Multiline);
                var matches = runtimeRegex.Matches(output);
                foreach (Match match in matches.Cast<Match>())
                {
                    if (match.Groups.Count > 1 && Version.TryParse(match.Groups[1].Value, out var installedVersion))
                    {
                        if (installedVersion.Major > requiredVersion.Major || (installedVersion.Major == requiredVersion.Major && installedVersion.Minor >= requiredVersion.Minor))
                        { Console.WriteLine($"Found compatible .NET Runtime: {installedVersion}"); return true; }
                    }
                }
                Console.WriteLine("No compatible .NET Runtime version found in 'dotnet --list-runtimes' output."); return false;
            }
            catch (System.ComponentModel.Win32Exception) { Console.WriteLine("Info: 'dotnet' command not found. Assuming .NET is not installed or not in system PATH."); return false; }
            catch (Exception ex) { Console.WriteLine($"Warning: Error checking .NET runtime: {ex.Message}"); return false; }
        }

        private static async Task<bool> InstallDotNetRuntime()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"dotnet-runtime-{DotNetVersionString}-installer.exe");
            Console.WriteLine($"Downloading .NET Runtime installer to: {tempPath}");
            try
            {
                using var client = new HttpClient(); client.DefaultRequestHeaders.Add("User-Agent", "ImagingTool/1.0"); await using var stream = await client.GetStreamAsync(DotNetRuntimeInstallerUrl); await using var fileStream = File.Create(tempPath); await stream.CopyToAsync(fileStream); Console.WriteLine("Download complete."); await fileStream.DisposeAsync();
                Console.WriteLine("Running .NET Runtime installer (may require elevation)...");
                var psi = new ProcessStartInfo { FileName = tempPath, Arguments = "/quiet /norestart", UseShellExecute = true, Verb = "runas" };
                using var process = Process.Start(psi); if (process == null) { Console.WriteLine("Error: Failed to start installer process."); return false; }
                await process.WaitForExitAsync();
                if (process.ExitCode == 0 || process.ExitCode == 3010) { Console.WriteLine($".NET Runtime installation completed (Exit Code: {process.ExitCode}). A restart might be needed."); return true; } else { Console.WriteLine($"Error: .NET Runtime installation failed with Exit Code: {process.ExitCode}"); return false; }
            }
            catch (HttpRequestException ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Error downloading .NET Runtime: {ex.Message}"); Console.WriteLine($"Please check the URL ({DotNetRuntimeInstallerUrl}) and your internet connection."); Console.ResetColor(); return false; }
            catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Error during .NET Runtime installation: {ex.Message}"); Console.ResetColor(); Console.WriteLine($"Consider installing manually from: {DotNetDownloadPageUrl}"); return false; }
            finally { if (File.Exists(tempPath)) { try { File.Delete(tempPath); } catch (IOException ex) { Console.WriteLine($"Warning: Could not delete temp file {tempPath}: {ex.Message}"); } } }
        }

        private static async Task<bool> CheckWimLib()
        {
            Console.WriteLine($"Checking for WimLib at: {WimlibPath}");
            if (!File.Exists(WimlibPath)) { Console.WriteLine("WimLib not found. Attempting to download and install..."); return await InstallWimLib(); }
            return true;
        }

        private static async Task<bool> InstallWimLib()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"wimlib-temp-{Guid.NewGuid()}"); var zipPath = Path.Combine(tempDir, "wimlib.zip"); Console.WriteLine($"Downloading WimLib from: {WimlibUrl}"); Console.WriteLine($"Temporary download location: {zipPath}");
            try
            {
                Directory.CreateDirectory(tempDir);
                using (var client = new HttpClient()) { client.DefaultRequestHeaders.Add("User-Agent", "ImagingTool/1.0"); Console.WriteLine("Starting download..."); var response = await client.GetAsync(WimlibUrl, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync(); await using var fileStream = File.Create(zipPath); await stream.CopyToAsync(fileStream); Console.WriteLine("Download complete."); }
                Console.WriteLine($"Extracting WimLib archive to: {tempDir}"); ZipFile.ExtractToDirectory(zipPath, tempDir, true);
                string? foundExePath = Directory.GetFiles(tempDir, "wimlib-imagex.exe", SearchOption.AllDirectories).FirstOrDefault(); if (string.IsNullOrEmpty(foundExePath)) { throw new FileNotFoundException($"'wimlib-imagex.exe' not found within the downloaded archive from {WimlibUrl}. The archive structure might have changed."); }
                string? sourceBinDir = Path.GetDirectoryName(foundExePath); if (sourceBinDir == null) { throw new DirectoryNotFoundException("Could not determine the source directory containing wimlib binaries."); }
                Console.WriteLine($"Copying WimLib binaries from '{sourceBinDir}' to target directory: {WimlibDir}"); Directory.CreateDirectory(WimlibDir);
                int filesCopied = 0; foreach (var file in Directory.GetFiles(sourceBinDir, "*.*", SearchOption.TopDirectoryOnly)) { string extension = Path.GetExtension(file).ToLowerInvariant(); if (extension == ".exe" || extension == ".dll") { var destPath = Path.Combine(WimlibDir, Path.GetFileName(file)); File.Copy(file, destPath, true); Console.WriteLine($"  Copied: {Path.GetFileName(file)}"); filesCopied++; } }
                if (filesCopied == 0) { throw new InvalidOperationException($"No .exe or .dll files were found to copy from '{sourceBinDir}'."); }
                if (!File.Exists(WimlibPath)) { throw new FileNotFoundException($"WimLib installation failed. Expected executable not found at the target path: {WimlibPath}"); }
                Console.WriteLine("WimLib installation successful."); return true;
            }
            catch (HttpRequestException ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Error downloading WimLib: {ex.Message}"); Console.WriteLine($"Please check the URL ({WimlibUrl}) and your internet connection."); Console.ResetColor(); return false; }
            catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Failed to install WimLib: {ex.Message}"); Console.ResetColor(); if (Directory.Exists(WimlibDir)) { try { Directory.Delete(WimlibDir, true); } catch (IOException ioEx) { Console.WriteLine($"Warning: Could not clean up target WimLib directory '{WimlibDir}': {ioEx.Message}"); } } return false; }
            finally { if (Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch (IOException ioEx) { Console.WriteLine($"Warning: Could not clean up temp directory '{tempDir}': {ioEx.Message}"); } } }
        }

        // --- Helper Method to Show Dialog on Dedicated STA Thread ---

        private static Task<string?> ShowSaveDialogOnStaThreadAsync()
        {
            // TaskCompletionSource allows us to await the result from the UI thread
            var tcs = new TaskCompletionSource<string?>();

            var uiThread = new Thread(() =>
            {
                try
                {
                    using (var dialog = new SaveFileDialog())
                    {
                        dialog.Title = "Select Backup Location and Filename";
                        dialog.Filter = "Windows Image Files (*.wim)|*.wim|All Files (*.*)|*.*";
                        dialog.DefaultExt = "wim";
                        dialog.FileName = $"SystemBackup_{DateTime.Now:yyyyMMdd_HHmmss}.wim";
                        dialog.RestoreDirectory = true;

                        // ShowDialog MUST run on this STA thread
                        DialogResult result = dialog.ShowDialog();

                        if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
                        {
                            // Set the result for the awaiting Task
                            tcs.TrySetResult(dialog.FileName);
                        }
                        else
                        {
                            // Set null result for cancellation
                            tcs.TrySetResult(null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Propagate any exception from the UI thread back to the awaiter
                    tcs.TrySetException(ex);
                }
            });

            // Crucial: Set the apartment state BEFORE starting the thread
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.IsBackground = true; // Allow application to exit even if this thread hangs
            uiThread.Start();

            // Return the Task that will complete when the UI thread sets the result
            return tcs.Task;
        }


        // --- Backup Logic ---

        private static async Task PerformSystemBackup()
        {
            Console.WriteLine("\n--- System Backup ---");
            string? destination = null;
            string? destinationDir = null;

            // --- Show Dialog using the helper method ---
            Console.WriteLine("Preparing file selection dialog...");
            // Await the result from the dedicated UI thread
            destination = await ShowSaveDialogOnStaThreadAsync();

            if (string.IsNullOrWhiteSpace(destination))
            {
                // User cancelled or an error occurred on the UI thread (exception would have been thrown)
                Console.WriteLine("Backup operation cancelled by user or dialog failed.");
                throw new OperationCanceledException("Backup cancelled via dialog.");
            }

            // --- Proceed with the selected destination ---
            destinationDir = Path.GetDirectoryName(destination);
            Console.WriteLine($"Selected backup destination: {destination}");

            // Basic validation (directory should exist after dialog)
            if (string.IsNullOrEmpty(destinationDir) || !Directory.Exists(destinationDir))
            {
                MessageBox.Show($"The selected directory '{destinationDir ?? "null"}' is invalid or does not exist.", "Invalid Directory", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Console.WriteLine($"Error: Invalid destination directory '{destinationDir ?? "null"}'.");
                throw new OperationCanceledException("Invalid directory selected.");
            }

            // Test if the destination directory is writable
            try
            {
                var testFileName = $"write_test_{Guid.NewGuid()}.tmp";
                var testFilePath = Path.Combine(destinationDir, testFileName);
                File.WriteAllText(testFilePath, "test"); // Sync is fine here
                File.Delete(testFilePath);
                Console.WriteLine($"Destination directory '{destinationDir}' is writable.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: Destination directory '{destinationDir}' is not writable. {ex.Message}");
                MessageBox.Show($"The selected directory '{destinationDir}' is not writable.\nPlease check permissions or choose a different location.\n\nError: {ex.Message}", "Directory Not Writable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new OperationCanceledException("Destination not writable.");
            }

            // Proceed with image creation using the selected destination
            await CreateSystemImage(destination);
        }

        // CreateSystemImage remains unchanged
        private static async Task CreateSystemImage(string destination)
        {
            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(systemDrive)) { throw new InvalidOperationException("Could not determine the system drive root (e.g., C:\\)."); }
            Console.WriteLine($"\nStarting backup of system drive '{systemDrive}' to '{destination}'...");
            Console.WriteLine("Using Volume Shadow Copy Service (VSS) for a consistent snapshot.");
            var threads = Environment.ProcessorCount;
            Console.WriteLine($"Using {threads} threads for compression.");
            var configFileName = $"wimlib-config-{Guid.NewGuid()}.txt";
            var configFilePath = Path.Combine(Path.GetTempPath(), configFileName);
            Console.WriteLine($"Using temporary config file for exclusions: {configFilePath}");
            bool wimlibReportedError = false;
            try
            {
                await File.WriteAllLinesAsync(configFilePath, new[] { "[ExclusionList]", @"\pagefile.sys", @"\swapfile.sys", @"\hiberfil.sys", @"\System Volume Information", @"\RECYCLER", @"\$Recycle.Bin", @"\Windows\Temp\*.*", @"\Windows\Temp", @"\Users\*\AppData\Local\Temp\*.*", @"\Users\*\AppData\Local\Temp", @"\Temp\*.*", @"\Temp" });
                var arguments = $"capture \"{systemDrive.TrimEnd('\\')}\" \"{destination}\" \"Windows System Backup\" \"Backup taken on {DateTime.Now:yyyy-MM-dd HH:mm:ss}\" --snapshot --config=\"{configFilePath}\" --compress=fast --threads={threads}";
                Console.WriteLine($"\nExecuting WimLib command:"); Console.WriteLine($"{WimlibPath} {arguments}\n");
                var psi = new ProcessStartInfo { FileName = WimlibPath, Arguments = arguments, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
                var startTime = DateTime.UtcNow; long totalBytesProcessed = 0L; long lastBytesProcessed = 0L; var lastUpdateTime = startTime; string lastFileName = "Initializing..."; object consoleLock = new object();
                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.OutputDataReceived += (sender, e) => { if (e.Data == null) { outputTcs.TrySetResult(true); return; } };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null) { errorTcs.TrySetResult(true); return; }
                    var errorData = e.Data;
                    if (errorData.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || errorData.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 || errorData.IndexOf("Cannot", StringComparison.OrdinalIgnoreCase) >= 0) { lock (consoleLock) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine($"\n[WimLib Error] {errorData}"); Console.ResetColor(); } wimlibReportedError = true; }
                    try
                    {
                        const string filePrefix = "Adding file: ["; if (errorData.StartsWith(filePrefix) && errorData.EndsWith("]")) { lastFileName = errorData.Substring(filePrefix.Length, errorData.Length - filePrefix.Length - 1); }
                        else if (errorData.Contains("GiB /", StringComparison.OrdinalIgnoreCase) && errorData.Contains("% done", StringComparison.OrdinalIgnoreCase))
                        {
                            var match = Regex.Match(errorData, @"(\d+(?:[.,]\d+)?)\s*GiB\s*/\s*(\d+(?:[.,]\d+)?)\s*GiB\s*\((\d+)\s*%\s*done\)", RegexOptions.IgnoreCase);
                            if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double processedGiB) && double.TryParse(match.Groups[2].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double totalGiB) && double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double percentage))
                            {
                                totalBytesProcessed = (long)(processedGiB * 1024 * 1024 * 1024); var now = DateTime.UtcNow; var elapsedTime = now - startTime; var timeSinceLastUpdate = (now - lastUpdateTime).TotalSeconds; double transferSpeedMbps = 0;
                                if (timeSinceLastUpdate > 0.2 && totalBytesProcessed > lastBytesProcessed) { var bytesSinceLastUpdate = totalBytesProcessed - lastBytesProcessed; transferSpeedMbps = (bytesSinceLastUpdate / (1024.0 * 1024.0)) / timeSinceLastUpdate; lastBytesProcessed = totalBytesProcessed; lastUpdateTime = now; } else if (timeSinceLastUpdate > 5) { lastUpdateTime = now; }
                                lock (consoleLock) { string progressLine = $"\rProgress: {percentage:F1}% ({processedGiB:F2}/{totalGiB:F2} GiB) | Speed: {transferSpeedMbps:F1} MB/s | Elapsed: {elapsedTime:hh\\:mm\\:ss} | File: {Truncate(lastFileName, 35)}"; Console.Write(new string(' ', Console.WindowWidth - 1) + "\r"); Console.Write(progressLine.PadRight(Console.WindowWidth - 1)); }
                            }
                        }
                    }
                    catch (Exception parseEx) { lock (consoleLock) { Console.WriteLine($"\n[Warning] Failed to parse wimlib output line: '{errorData}'. Error: {parseEx.Message}"); } }
                };
                if (!process.Start()) { throw new InvalidOperationException($"Failed to start WimLib process: {WimlibPath}"); }
                process.BeginOutputReadLine(); process.BeginErrorReadLine(); await process.WaitForExitAsync(); await Task.WhenAll(outputTcs.Task, errorTcs.Task);
                lock (consoleLock) { Console.Write(new string(' ', Console.WindowWidth - 1) + "\r"); }
                Console.WriteLine("\nWimLib process finished.");
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
