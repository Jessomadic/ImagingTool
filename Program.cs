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
                throw new InvalidOperationException("Invalid destination path specified.");

            await CreateSystemImage(destination);
        }

        // Primary backup routine with skipped file detection and robust retry logic.
        private static async Task CreateSystemImage(string destination)
        {
            var sourceDrive = @"C:\";
            var logicalProcessorCount = Environment.ProcessorCount;
            var skippedFiles = new List<string>();

            var psi = new ProcessStartInfo
            {
                FileName = WimlibPath,
                Arguments = $"capture {sourceDrive} {destination} " +
                            $"\"Backup Image\" \"System Backup\" " +
                            $"--snapshot " +
                            $"--no-acls " +
                            $"--threads={logicalProcessorCount}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            var startTime = DateTime.Now;
            var totalBytesProcessed = 0L;
            using var process = new Process { StartInfo = psi };

            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                    return;
                Console.WriteLine(e.Data);

                if (e.Data.Contains("Processed"))
                {
                    var parts = e.Data.Split(' ');
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var bytes))
                    {
                        totalBytesProcessed = bytes;
                        var elapsedTime = (DateTime.Now - startTime).TotalSeconds;
                        var rate = totalBytesProcessed / elapsedTime;
                        Console.WriteLine($"\rBackup Rate: {rate / 1024 / 1024:F2} MB/s | Total: {totalBytesProcessed / 1024 / 1024:F2} MB");
                    }
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                    return;
                Console.WriteLine($"Error: {e.Data}");

                // When encountering write-protection errors, extract and process the file path.
                if (e.Data.Contains("Access is denied") || e.Data.Contains("write-protected"))
                {
                    string path = ExtractFilePathFromError(e.Data);
                    if (!string.IsNullOrEmpty(path))
                    {
                        if (IsCloudOnly(path))
                        {
                            Console.WriteLine($"Cloud file detected; skipping: {path}");
                        }
                        else
                        {
                            if (!skippedFiles.Contains(path))
                            {
                                skippedFiles.Add(path);
                                Console.WriteLine($"Local file scheduled for retry: {path}");
                            }
                        }
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            // Continue regardless of the main capture process exit code.
            if (skippedFiles.Any())
            {
                Console.WriteLine("The following local files were skipped due to access issues:");
                foreach (var file in skippedFiles)
                {
                    Console.WriteLine(file);
                }
                Console.WriteLine("Retrying skipped local files...");
                foreach (var file in skippedFiles)
                {
                    await RetryBackupForFile(file, destination);
                }
            }
            else
            {
                Console.WriteLine("\nBackup completed with no local access issues!");
            }
        }

        // Retry backup for a specific file using an update command.
        private static async Task RetryBackupForFile(string filePath, string destination, int maxAttempts = 3)
        {
            // Skip retries if the file is cloud-only.
            if (IsCloudOnly(filePath))
            {
                Console.WriteLine($"Skipping retry for cloud-only file: {filePath}");
                return;
            }

            int attempt = 1;
            bool success = false;
            while (attempt <= maxAttempts && !success)
            {
                Console.WriteLine($"Retrying file: {filePath} (attempt {attempt})");
                var psi = new ProcessStartInfo
                {
                    FileName = WimlibPath,
                    // Adjust update command parameters (e.g., image index) as needed.
                    Arguments = $"update \"{destination}\" 1 \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                Console.WriteLine(output);
                if (!string.IsNullOrWhiteSpace(error))
                    Console.WriteLine($"Retry Error: {error}");

                if (process.ExitCode == 0)
                {
                    Console.WriteLine($"Successfully backed up {filePath} on attempt {attempt}.");
                    success = true;
                }
                else
                {
                    attempt++;
                    await Task.Delay(1000); // Delay before retrying.
                }
            }

            if (!success)
            {
                Console.WriteLine($"Final failure in backing up {filePath} after {maxAttempts} attempts.");
                // Log the failure for further review.
                File.AppendAllText("skipped.log", $"{filePath}{Environment.NewLine}");
            }
        }

        // Checks if a file is cloud-only.
        // If the file doesn't exist locally or has the RECALL_ON_OPEN attribute, assume it is cloud-only.
        private static bool IsCloudOnly(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    // If the file does not exist locally, assume it's cloud-only.
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

        // Extracts a file or folder path from an error message by looking for quoted substrings.
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
