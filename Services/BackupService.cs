using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ImagingTool.Helpers;

namespace ImagingTool.Services
{
    public class BackupService
    {
        private readonly AppSettings _settings;
        private PerformanceCounter? _sourceDiskReadCounter;
        private PerformanceCounter? _destDiskWriteCounter;

        public BackupService(AppSettings settings)
        {
            _settings = settings;
        }

        public void InitializePerformanceCounters(string sourceDrive, string destDrive)
        {
            Console.WriteLine("Initializing performance counters...");
            try
            {
                const string categoryName = "PhysicalDisk";
                const string readCounterName = "Disk Read Bytes/sec";
                const string writeCounterName = "Disk Write Bytes/sec";

                var category = new PerformanceCounterCategory(categoryName);
                string[] instanceNames = category.GetInstanceNames();

                string? sourceInstance = instanceNames.FirstOrDefault(n => n.Contains($" {sourceDrive.Trim(':')}"));
                string? destInstance = instanceNames.FirstOrDefault(n => n.Contains($" {destDrive.Trim(':')}"));

                if (string.IsNullOrEmpty(sourceInstance))
                    sourceInstance = instanceNames.FirstOrDefault(n => n.EndsWith(" 0"));
                if (string.IsNullOrEmpty(destInstance))
                    destInstance = instanceNames.FirstOrDefault(n => n.EndsWith(" 1"));

                if (!string.IsNullOrEmpty(sourceInstance))
                {
                    _sourceDiskReadCounter = new PerformanceCounter(categoryName, readCounterName, sourceInstance, readOnly: true);
                    _sourceDiskReadCounter.NextValue();
                    Console.WriteLine($"Monitoring Read Speed for Disk: {sourceInstance}");
                }
                else
                {
                    Console.WriteLine($"Warning: Could not find performance counter instance for source drive {sourceDrive}. Read speed monitoring disabled.");
                }

                if (!string.IsNullOrEmpty(destInstance))
                {
                    _destDiskWriteCounter = new PerformanceCounter(categoryName, writeCounterName, destInstance, readOnly: true);
                    _destDiskWriteCounter.NextValue();
                    Console.WriteLine($"Monitoring Write Speed for Disk: {destInstance}");
                }
                else
                {
                    Console.WriteLine($"Warning: Could not find performance counter instance for destination drive {destDrive}. Write speed monitoring disabled.");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Warning: Failed to initialize performance counters. Disk speed monitoring disabled.");
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                _sourceDiskReadCounter?.Dispose();
                _sourceDiskReadCounter = null;
                _destDiskWriteCounter?.Dispose();
                _destDiskWriteCounter = null;
            }
        }

        public async Task PerformSystemBackup(string destination)
        {
            Console.WriteLine("\n--- System Backup ---");

            string? destinationDir = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(destinationDir))
                throw new ArgumentException("Invalid destination path (no directory).", nameof(destination));

            Console.WriteLine($"Proceeding with backup to: {destination}");
            await CreateSystemImage(destination);
        }

        private async Task CreateSystemImage(string destination)
        {
            string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(systemDrive))
                throw new InvalidOperationException("Could not determine the system drive root.");

            int threads = Environment.ProcessorCount;
            Console.WriteLine($"\nStarting backup of system drive '{systemDrive}' to '{destination}'...");
            Console.WriteLine("Using Volume Shadow Copy Service (VSS).");
            Console.WriteLine($"Using {threads} threads.");

            (string compressionArg, string compressionLevelDisplay) = ResolveCompressionLevel(_settings.WimCompressionLevel);
            Console.WriteLine($"Using Compression Level: {compressionLevelDisplay}");

            var configFilePath = Path.Combine(Path.GetTempPath(), $"wimlib-config-{Guid.NewGuid()}.txt");
            Console.WriteLine($"Using temporary config file for exclusions: {configFilePath}");

            bool wimlibReportedError = false;

            try
            {
                await WriteExclusionConfig(configFilePath);

                var arguments =
                    $"capture \"{systemDrive.TrimEnd('\\')}\" \"{destination}\" " +
                    $"\"Windows System Backup\" \"Backup taken on {DateTime.Now:yyyy-MM-dd HH:mm:ss}\" " +
                    $"--snapshot --config=\"{configFilePath}\" --compress={compressionArg} --threads={threads}";

                Console.WriteLine($"\nExecuting WimLib command:");
                Console.WriteLine($"{_settings.WimlibPath} {arguments}\n");

                var psi = new ProcessStartInfo
                {
                    FileName = _settings.WimlibPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                var startTime = DateTime.UtcNow;
                long totalBytesProcessed = 0L;
                long lastBytesProcessed = 0L;
                var lastUpdateTime = startTime;
                double lastKnownSpeedMbps = 0;
                string lastFileName = "Initializing...";
                object consoleLock = new object();

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) { outputTcs.TrySetResult(true); return; }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) { errorTcs.TrySetResult(true); return; }

                    string line = e.Data;

                    if (IsWimlibError(line))
                    {
                        if (line.Contains("Parent inode") && line.Contains("was missing from the MFT listing"))
                        {
                            lock (consoleLock)
                            {
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"\n[WimLib Warning] MFT inconsistency (continuing): {line}");
                                Console.ResetColor();
                            }
                        }
                        else
                        {
                            lock (consoleLock)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.Error.WriteLine($"\n[WimLib Error] {line}");
                                Console.ResetColor();
                            }
                            wimlibReportedError = true;
                        }
                        return;
                    }

                    const string filePrefix = "Adding file: [";
                    if (line.StartsWith(filePrefix) && line.EndsWith("]"))
                    {
                        lastFileName = line.Substring(filePrefix.Length, line.Length - filePrefix.Length - 1);
                        return;
                    }

                    var match = Regex.Match(line,
                        @"(\d+(?:[.,]\d+)?)\s*GiB\s*/\s*(\d+(?:[.,]\d+)?)\s*GiB\s*\((\d+)\s*%\s*done\)",
                        RegexOptions.IgnoreCase);

                    if (!match.Success) return;
                    if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double processedGiB)) return;
                    if (!double.TryParse(match.Groups[2].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double totalGiB)) return;
                    if (!double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double percentage)) return;

                    totalBytesProcessed = (long)(processedGiB * 1024 * 1024 * 1024);
                    var now = DateTime.UtcNow;
                    var elapsedTime = now - startTime;
                    var timeSinceLastUpdate = (now - lastUpdateTime).TotalSeconds;

                    if (timeSinceLastUpdate > 0.2 && totalBytesProcessed > lastBytesProcessed)
                    {
                        lastKnownSpeedMbps = (totalBytesProcessed - lastBytesProcessed) / (1024.0 * 1024.0) / timeSinceLastUpdate;
                        lastBytesProcessed = totalBytesProcessed;
                        lastUpdateTime = now;
                    }
                    else if (timeSinceLastUpdate > 5)
                    {
                        lastUpdateTime = now;
                    }

                    double remainingGiB = totalGiB - processedGiB;

                    string etaStr = "--:--:--";
                    if (lastKnownSpeedMbps > 0)
                    {
                        var eta = TimeSpan.FromSeconds(remainingGiB * 1024.0 / lastKnownSpeedMbps);
                        etaStr = eta.ToString(@"h\:mm\:ss");
                    }

                    string estimatedWimStr = "calculating...";
                    try
                    {
                        long wimBytes = new FileInfo(destination).Length;
                        if (wimBytes > 0 && processedGiB > 0.01)
                        {
                            double wimSizeGiB = wimBytes / (1024.0 * 1024.0 * 1024.0);
                            double estimatedFinalGiB = (wimSizeGiB / processedGiB) * totalGiB;
                            estimatedWimStr = $"~{estimatedFinalGiB:F2} GiB";
                        }
                    }
                    catch { }

                    float readMBps = _sourceDiskReadCounter?.NextValue() / (1024f * 1024f) ?? 0f;
                    float writeMBps = _destDiskWriteCounter?.NextValue() / (1024f * 1024f) ?? 0f;

                    lock (consoleLock)
                    {
                        string progressLine =
                            $"\r{percentage:F1}% | {processedGiB:F2}/{totalGiB:F2} GiB" +
                            $" | Left: {remainingGiB:F2} GiB" +
                            $" | {lastKnownSpeedMbps:F1} MB/s" +
                            $" | ETA: {etaStr}" +
                            $" | Est. WIM: {estimatedWimStr}" +
                            $" | R:{readMBps:F0} W:{writeMBps:F0} MB/s" +
                            $" | {elapsedTime:h\\:mm\\:ss}" +
                            $" | {VolumeHelper.Truncate(lastFileName, 35)}";
                        Console.Write(new string(' ', Console.WindowWidth - 1) + "\r");
                        Console.Write(progressLine.PadRight(Console.WindowWidth - 1));
                    }
                };

                if (!process.Start())
                    throw new InvalidOperationException($"Failed to start WimLib process: {_settings.WimlibPath}");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();
                await Task.WhenAll(outputTcs.Task, errorTcs.Task);

                lock (consoleLock)
                {
                    Console.Write(new string(' ', Console.WindowWidth - 1) + "\r");
                }

                Console.WriteLine("\nWimLib process finished.");

                if (process.ExitCode != 0)
                    throw new Exception($"WimLib process exited with error code: {process.ExitCode}. Check logs above.");

                if (wimlibReportedError)
                    throw new Exception("WimLib reported one or more errors during execution (see logs above). Backup may be incomplete or corrupted.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n--- Error during backup creation ---");
                Console.WriteLine($"Message: {ex.Message}");
                Console.ResetColor();
                throw;
            }
            finally
            {
                if (File.Exists(configFilePath))
                {
                    try
                    {
                        File.Delete(configFilePath);
                        Console.WriteLine($"Deleted temporary config file: {configFilePath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Could not delete temporary config file '{configFilePath}'. {ex.Message}");
                    }
                }

                _sourceDiskReadCounter?.Dispose();
                _destDiskWriteCounter?.Dispose();
            }
        }

        internal static (string arg, string display) ResolveCompressionLevel(string? compressionLevel)
        {
            return compressionLevel?.ToLowerInvariant() switch
            {
                "none" => ("none", "None (Fastest, Largest File)"),
                "maximum" => ("lzx", "Maximum (Slowest, Smallest File)"),
                _ => ("fast", "Fast (Balanced)")
            };
        }

        internal static async Task WriteExclusionConfig(string configFilePath)
        {
            var exclusions = new List<string>
            {
                "[ExclusionList]",
                @"\pagefile.sys",
                @"\swapfile.sys",
                @"\hiberfil.sys",
                @"\System Volume Information",
                @"\RECYCLER",
                @"\$Recycle.Bin",
                @"\Windows\Temp\*.*",
                @"\Windows\Temp",
                @"\Users\*\AppData\Local\Temp\*.*",
                @"\Users\*\AppData\Local\Temp",
                @"\Temp\*.*",
                @"\Temp",
                @"\b042787fde8c8f3f_0"
            };
            await File.WriteAllLinesAsync(configFilePath, exclusions);
        }

        internal static bool IsWimlibError(string line)
        {
            return line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("Cannot", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
