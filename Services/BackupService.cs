using System.Diagnostics;
using System.Text;

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

            // Solid mode bundles many small files into large blocks before compressing.
            // On a network destination this dramatically reduces write volume and is almost
            // always faster overall, even accounting for the extra CPU work.
            bool useSolid = !compressionArg.Equals("none", StringComparison.OrdinalIgnoreCase);
            string solidFlags = useSolid ? " --solid --solid-chunk-size=64M" : "";
            if (useSolid) Console.WriteLine("Solid mode: ON (64 MiB blocks)");

            var configFilePath = Path.Combine(Path.GetTempPath(), $"wimlib-config-{Guid.NewGuid()}.txt");
            Console.WriteLine($"Using temporary config file for exclusions: {configFilePath}");

            bool wimlibReportedError = false;

            try
            {
                await WriteExclusionConfig(configFilePath);

                var arguments =
                    $"capture \"{systemDrive.TrimEnd('\\')}\" \"{destination}\" " +
                    $"\"Windows System Backup\" \"Backup taken on {DateTime.Now:yyyy-MM-dd HH:mm:ss}\" " +
                    $"--snapshot --config=\"{configFilePath}\" --compress={compressionArg}{solidFlags} --threads={threads}";

                Console.WriteLine($"\nExecuting WimLib command:");
                Console.WriteLine($"{_settings.WimlibPath} {arguments}\n");

                // Do NOT redirect stdout. wimlib calls GetConsoleMode() on its stdout handle —
                // if that check fails (because stdout is a pipe) wimlib suppresses all \r-based
                // progress output entirely. By inheriting our real console handle, wimlib detects
                // a true console and writes its native progress directly to the terminal.
                // We only redirect stderr so we can capture errors and warnings.
                var psi = new ProcessStartInfo
                {
                    FileName = _settings.WimlibPath,
                    Arguments = arguments,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                var startTime = DateTime.UtcNow;

                using var process = new Process { StartInfo = psi };

                if (!process.Start())
                    throw new InvalidOperationException($"Failed to start WimLib process: {_settings.WimlibPath}");

                // Read stderr for error and warning detection only.
                // wimlib writes errors to stderr; progress goes to stdout (→ our real console).
                var stderrTask = Task.Run(async () =>
                {
                    var sb = new StringBuilder();
                    var buf = new char[4096];
                    int n;
                    while ((n = await process.StandardError.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            char c = buf[i];
                            if (c == '\r' || c == '\n')
                            {
                                if (sb.Length > 0)
                                {
                                    string line = sb.ToString();
                                    sb.Clear();
                                    HandleStderrLine(line, ref wimlibReportedError);
                                }
                            }
                            else
                            {
                                sb.Append(c);
                            }
                        }
                    }
                    if (sb.Length > 0)
                        HandleStderrLine(sb.ToString(), ref wimlibReportedError);
                });

                // Print disk I/O and WIM file size every 15 seconds as a supplemental stat line.
                // Uses a leading newline so it doesn't trample wimlib's \r progress line.
                using var statsCts = new CancellationTokenSource();
                var statsTask = Task.Run(async () =>
                {
                    while (true)
                    {
                        try { await Task.Delay(15_000, statsCts.Token); }
                        catch (OperationCanceledException) { return; }

                        float readMBps = (_sourceDiskReadCounter?.NextValue() ?? 0f) / (1024f * 1024f);
                        float writeMBps = (_destDiskWriteCounter?.NextValue() ?? 0f) / (1024f * 1024f);
                        var elapsed = DateTime.UtcNow - startTime;

                        string wimInfo = "";
                        try
                        {
                            long wimBytes = new FileInfo(destination).Length;
                            if (wimBytes > 0)
                                wimInfo = $" | WIM: {wimBytes / (1024.0 * 1024.0 * 1024.0):F2} GiB";
                        }
                        catch { }

                        Console.WriteLine($"\n  [{elapsed:h\\:mm\\:ss}] Read: {readMBps:F0} MB/s | Write: {writeMBps:F0} MB/s{wimInfo}");
                    }
                });

                await process.WaitForExitAsync();
                statsCts.Cancel();
                await stderrTask;
                try { await statsTask; } catch (OperationCanceledException) { }

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

        private static void HandleStderrLine(string line, ref bool wimlibReportedError)
        {
            if (line.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[WimLib Warning] {line}");
                Console.ResetColor();
                return;
            }

            if (IsWimlibError(line))
            {
                if (line.Contains("Parent inode") && line.Contains("was missing from the MFT listing"))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[WimLib Warning] MFT inconsistency (continuing): {line}");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"\n[WimLib Error] {line}");
                    Console.ResetColor();
                    wimlibReportedError = true;
                }
            }
        }

        internal static (string arg, string display) ResolveCompressionLevel(string? compressionLevel)
        {
            return compressionLevel?.ToLowerInvariant() switch
            {
                "none"    => ("none", "None (No compression — fastest write, largest file)"),
                "maximum" => ("lzms", "Maximum — LZMS Solid (best compression, recommended for network)"),
                _         => ("fast", "Fast — XPRESS Solid (balanced)"),
            };
        }

        internal static async Task WriteExclusionConfig(string configFilePath)
        {
            var exclusions = new List<string>
            {
                "[ExclusionList]",

                // --- Virtual memory / hibernation ---
                @"\pagefile.sys",
                @"\swapfile.sys",
                @"\hiberfil.sys",

                // --- System junk ---
                @"\System Volume Information",
                @"\RECYCLER",
                @"\$Recycle.Bin",
                @"\DumpStack.log",
                @"\DumpStack.log.tmp",

                // --- Memory / crash dumps ---
                @"\Windows\Minidump",
                @"\Windows\memory.dmp",
                @"\Users\*\AppData\Local\CrashDumps",

                // --- Temp directories ---
                @"\Windows\Temp",
                @"\Users\*\AppData\Local\Temp",
                @"\Temp",

                // --- Windows Update cache (re-downloaded on demand) ---
                @"\Windows\SoftwareDistribution\Download",

                // --- Windows upgrade staging (can be GBs) ---
                @"\$Windows.~BT",
                @"\$Windows.~WS",
                @"\Windows.old",

                // --- Windows logs ---
                @"\Windows\Logs",
                @"\Windows\CbsTemp",

                // --- Windows prefetch (auto-rebuilt) ---
                @"\Windows\Prefetch",

                // --- Thumbnail / icon caches ---
                @"\Users\*\AppData\Local\Microsoft\Windows\Explorer",

                // --- Windows web / internet caches ---
                @"\Users\*\AppData\Local\Microsoft\Windows\INetCache",
                @"\Users\*\AppData\Local\Microsoft\Windows\WebCache",

                // --- Windows Error Reporting ---
                @"\ProgramData\Microsoft\Windows\WER",
                @"\Users\*\AppData\Local\Microsoft\Windows\WER",

                // --- Windows Defender (definitions re-downloaded; scan cache rebuilt) ---
                @"\ProgramData\Microsoft\Windows Defender\Scans",

                // --- Chrome / Chromium caches ---
                @"\Users\*\AppData\Local\Google\Chrome\User Data\*\Cache",
                @"\Users\*\AppData\Local\Google\Chrome\User Data\*\Code Cache",
                @"\Users\*\AppData\Local\Google\Chrome\User Data\*\GPUCache",

                // --- Edge caches ---
                @"\Users\*\AppData\Local\Microsoft\Edge\User Data\*\Cache",
                @"\Users\*\AppData\Local\Microsoft\Edge\User Data\*\Code Cache",
                @"\Users\*\AppData\Local\Microsoft\Edge\User Data\*\GPUCache",

                // --- Brave caches ---
                @"\Users\*\AppData\Local\BraveSoftware\Brave-Browser\User Data\*\Cache",
                @"\Users\*\AppData\Local\BraveSoftware\Brave-Browser\User Data\*\Code Cache",
                @"\Users\*\AppData\Local\BraveSoftware\Brave-Browser\User Data\*\GPUCache",

                // --- Firefox caches ---
                @"\Users\*\AppData\Local\Mozilla\Firefox\Profiles\*\cache2",
                @"\Users\*\AppData\Local\Mozilla\Firefox\Profiles\*\startupCache",

                // --- Teams caches ---
                @"\Users\*\AppData\Local\Microsoft\Teams\*\Cache",
                @"\Users\*\AppData\Local\Microsoft\Teams\*\Code Cache",
                @"\Users\*\AppData\Local\Microsoft\Teams\*\GPUCache",
                @"\Users\*\AppData\Roaming\Microsoft\Teams\*\Cache",
                @"\Users\*\AppData\Roaming\Microsoft\Teams\*\blob_storage",
                @"\Users\*\AppData\Roaming\Microsoft\Teams\*\GPUCache",

                // --- Slack caches ---
                @"\Users\*\AppData\Roaming\Slack\Cache",
                @"\Users\*\AppData\Roaming\Slack\Code Cache",
                @"\Users\*\AppData\Local\slack\*\Cache",

                // --- Discord caches ---
                @"\Users\*\AppData\Local\Discord\*\Cache",
                @"\Users\*\AppData\Local\Discord\*\Code Cache",
                @"\Users\*\AppData\Local\Discord\*\GPUCache",

                // --- Spotify cache ---
                @"\Users\*\AppData\Local\Spotify\Storage",

                // --- Steam shader / download caches ---
                @"\Users\*\AppData\Local\Steam\htmlcache",
                @"\Program Files (x86)\Steam\steamapps\downloading",
                @"\Program Files (x86)\Steam\steamapps\temp",

                // --- Visual Studio / JetBrains caches ---
                @"\Users\*\AppData\Local\Microsoft\VisualStudio\*\ComponentModelCache",
                @"\Users\*\AppData\Local\JetBrains\*\caches",

                // --- npm / yarn / pip package caches ---
                @"\Users\*\AppData\Roaming\npm-cache",
                @"\Users\*\AppData\Local\pip\Cache",
                @"\Users\*\AppData\Local\Yarn\Cache",

                // --- NuGet package cache (restored by dotnet restore) ---
                @"\Users\*\.nuget\packages",

                // --- Office file cache ---
                @"\Users\*\AppData\Local\Microsoft\Office\*\OfficeFileCache",

                // --- Windows Store package caches ---
                @"\Users\*\AppData\Local\Packages\*\LocalCache",
                @"\Users\*\AppData\Local\Packages\*\TempState",

                // --- Visual C++ / installer package caches ---
                @"\ProgramData\Package Cache",

                // --- OneDrive cloud-only stubs (unreadable from VSS on read-only volume) ---
                // Files set to "online-only" in OneDrive are placeholders with no local data;
                // trying to read them from a VSS snapshot fails with c000cf0c and causes wimlib
                // to abort after too many retries.
                @"\Users\*\OneDrive",
                @"\Users\*\OneDrive - *",

                // --- Misc artefact from previous builds ---
                @"\b042787fde8c8f3f_0",
            };
            await File.WriteAllLinesAsync(configFilePath, exclusions);
        }

        internal static bool IsWimlibError(string line)
        {
            // Exclude [WARNING] lines — they are non-fatal and handled separately.
            if (line.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase))
                return false;

            return line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("Cannot", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
