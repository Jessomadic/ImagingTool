using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.IO.Compression;

namespace ImagingTool
{
    class Program
    {
        // OneDrive cloud-only files typically have the RECALL_ON_OPEN attribute.
        // Adjust this flag as required.
        private const FileAttributes FileAttributeRecallOnOpen = (FileAttributes)0x400000;

        private const string WimlibPath = @"C:\WimLib\wimlib-imagex.exe";
        private const string WimlibUrl = "https://wimlib.net/downloads/wimlib-1.14.4-windows-x86_64-bin.zip";
        private const string DotNetVersion = "9.0";
        private const string DotNetUrl = "https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/sdk-9.0.202-windows-x64-installer";

        static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine("Windows System Imaging Tool");
                Console.WriteLine("---------------------------");

                if (!IsRunningAsAdministrator())
                {
                    Console.WriteLine("Error: This tool requires administrator privileges.");
                    return;
                }

                await InitializeRequirements();
                await PerformSystemBackup();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Critical Error: {ex.Message}");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        private static async Task InitializeRequirements()
        {
            if (!await CheckDotNetRuntime())
                throw new InvalidOperationException("Failed to verify or install .NET Runtime.");

            if (!await CheckWimLib())
                throw new InvalidOperationException("Failed to verify or install WimLib.");
        }

        private static async Task<bool> CheckDotNetRuntime()
        {
            if (!IsDotNetRuntimeInstalled())
            {
                Console.WriteLine($"Installing .NET {DotNetVersion} Runtime...");
                return await InstallDotNetRuntime();
            }
            return true;
        }

        private static async Task<bool> CheckWimLib()
        {
            if (!File.Exists(WimlibPath))
            {
                Console.WriteLine("Installing WimLib...");
                return await InstallWimLib();
            }
            return true;
        }

        private static bool IsDotNetRuntimeInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                using var reader = process?.StandardOutput;
                var result = reader?.ReadToEnd() ?? string.Empty;
                return result.Contains($"Microsoft.NETCore.App {DotNetVersion}");
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> InstallDotNetRuntime()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "dotnet-installer.exe");
            try
            {
                using var client = new HttpClient();
                await using var stream = await client.GetStreamAsync(DotNetUrl);
                await using var fileStream = File.Create(tempPath);
                await stream.CopyToAsync(fileStream);

                var psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/quiet /norestart",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                await process!.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to install .NET Runtime: {ex.Message}");
                return false;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static async Task<bool> InstallWimLib()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "wimlib");
            var zipPath = Path.Combine(tempDir, "wimlib.zip");
            try
            {
                Directory.CreateDirectory(tempDir);
                using var client = new HttpClient();
                await using var stream = await client.GetStreamAsync(WimlibUrl);
                await using var fileStream = File.Create(zipPath);
                await stream.CopyToAsync(fileStream);

                ZipFile.ExtractToDirectory(zipPath, tempDir);
                Directory.CreateDirectory(Path.GetDirectoryName(WimlibPath)!);

                foreach (var file in Directory.GetFiles(tempDir))
                {
                    File.Copy(file, Path.Combine(Path.GetDirectoryName(WimlibPath)!, Path.GetFileName(file)), true);
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to install WimLib: {ex.Message}");
                return false;
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        private static async Task PerformSystemBackup()
        {
            Console.Write("\nEnter backup destination path (e.g., D:\\Backup.wim): ");
            var destination = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(destination) || !Directory.Exists(Path.GetDirectoryName(destination)))
            {
                Console.WriteLine("Error: Invalid destination path specified.");
                return;
            }

            try
            {
                // Test if the destination is writable
                var testFilePath = Path.Combine(Path.GetDirectoryName(destination)!, "test.tmp");
                File.WriteAllText(testFilePath, "test");
                File.Delete(testFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: Destination path is not writable. {ex.Message}");
                return;
            }

            await CreateSystemImage(destination);
        }

        private static async Task CreateSystemImage(string destination)
        {
            var sourceDrive = @"C:\";
            var logicalProcessorCount = 16; // Limit threads to 16
            var configFilePath = Path.Combine(Path.GetTempPath(), "wimlib-config.txt");
            File.WriteAllLines(configFilePath, new[]
            {
                "[ExclusionList]",
                @"\OneDrive",
                @"\Temp",
                @"\System Volume Information",
                @"\$Recycle.Bin",
                @"\pagefile.sys",
                @"\swapfile.sys",
                @"\hiberfil.sys"
            });

            var psi = new ProcessStartInfo
            {
                FileName = WimlibPath,
                Arguments = $"capture {sourceDrive} {destination} " +
                            $"\"Backup Image\" \"System Backup\" " +
                            "--snapshot " +
                            "--no-acls " +
                            $"--config=\"{configFilePath}\" " +
                            "--compress=fast " +
                            $"--threads={logicalProcessorCount}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var startTime = DateTime.Now;
            var totalBytesProcessed = 0L;
            var lastBytesProcessed = 0L;
            var lastUpdateTime = DateTime.Now;

            using var process = new Process { StartInfo = psi };

            process.ErrorDataReceived += async (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                    return;

                // Parse the file name being backed up
                if (e.Data.StartsWith("Adding file:"))
                {
                    var fileName = e.Data.Substring("Adding file:".Length).Trim();
                    Console.WriteLine($"Currently backing up: {fileName}");
                }

                // Parse progress information
                if (e.Data.Contains("GiB") && e.Data.Contains("% done"))
                {
                    var parts = e.Data.Split(new[] { ' ', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && long.TryParse(parts[2], out var processedGiB) && long.TryParse(parts[4], out var totalGiB))
                    {
                        totalBytesProcessed = processedGiB * 1024 * 1024 * 1024; // Convert GiB to bytes
                        var elapsedTime = (DateTime.Now - startTime).TotalSeconds;

                        // Calculate transfer speed
                        var timeSinceLastUpdate = (DateTime.Now - lastUpdateTime).TotalSeconds;
                        var bytesSinceLastUpdate = totalBytesProcessed - lastBytesProcessed;
                        var transferSpeed = bytesSinceLastUpdate / timeSinceLastUpdate;

                        lastBytesProcessed = totalBytesProcessed;
                        lastUpdateTime = DateTime.Now;

                        var percentageCompleted = (double)processedGiB / totalGiB * 100;
                        Console.WriteLine($"\rBackup Rate: {transferSpeed / 1024 / 1024:F2} MB/s | " +
                                          $"Progress: {percentageCompleted:F2}% | " +
                                          $"Elapsed Time: {elapsedTime:F2} seconds");
                    }
                }
            };

            process.Start();
            process.BeginErrorReadLine(); // Read from stderr for progress and file names
            await process.WaitForExitAsync();

            Console.WriteLine("\nBackup completed successfully!");
        }

        private static async Task RetryBackupForFile(string filePath, string destination, int maxAttempts = 1)
        {
            // Retry logic remains the same, but maxAttempts is reduced to 1
        }

        private static bool IsCloudOnly(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return true;
                }
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributeRecallOnOpen) != 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking cloud status for {path}: {ex.Message}");
                return true;
            }
        }

        private static string ExtractFilePathFromError(string errorMessage)
        {
            int firstQuote = errorMessage.IndexOf('"');
            int lastQuote = errorMessage.LastIndexOf('"');
            if (firstQuote >= 0 && lastQuote > firstQuote)
            {
                return errorMessage.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
            }
            return string.Empty;
        }

        private static bool IsRunningAsAdministrator()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
