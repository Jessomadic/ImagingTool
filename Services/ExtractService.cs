using System.Diagnostics;
using Microsoft.Win32;
using ImagingTool.Helpers;

namespace ImagingTool.Services
{
    public class ExtractService
    {
        private readonly AppSettings _settings;
        private readonly IProcessRunner _processRunner;

        private const string TempHiveKey = "IMGTOOL_SW";

        // File system paths to extract from the WIM (relative to image root)
        private static readonly string[] FileSystemPaths =
        {
            @"\Users",
            @"\Program Files",
            @"\Program Files (x86)",
            @"\ProgramData",
        };

        // HKLM\SOFTWARE\Microsoft subkeys worth preserving for app compatibility
        private static readonly string[] MicrosoftAppSubKeys =
        {
            @"Windows\CurrentVersion\Uninstall",
            @"Windows\CurrentVersion\App Paths",
            @"Windows\CurrentVersion\SharedDLLs",
            @"Windows NT\CurrentVersion\Fonts",
            @"Windows NT\CurrentVersion\FontSubstitutes",
        };

        // Top-level HKLM\SOFTWARE keys handled separately or skipped entirely
        private static readonly HashSet<string> SkipTopLevelKeys =
            new(StringComparer.OrdinalIgnoreCase) { "Classes", "Microsoft", "WOW6432Node" };

        private static readonly string RegExePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");

        public ExtractService(AppSettings settings, IProcessRunner processRunner)
        {
            _settings = settings;
            _processRunner = processRunner;
        }

        public async Task PerformExtraction(string sourceWim, string targetRoot)
        {
            Console.WriteLine("\n--- Data Extraction ---");
            Console.WriteLine($"Source WIM : {sourceWim}");
            Console.WriteLine($"Target Root: {targetRoot}");

            await ExtractFileSystem(sourceWim, targetRoot);
            await MergeHklmRegistry(sourceWim);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nExtraction complete.");
            Console.ResetColor();
            Console.WriteLine("Note: Per-user settings (HKCU) are stored in NTUSER.DAT inside");
            Console.WriteLine("      each extracted user folder. They take effect after");
            Console.WriteLine("      logging out and back in with the matching username.");
        }

        private async Task ExtractFileSystem(string sourceWim, string targetRoot)
        {
            Console.WriteLine("\n--- Extracting file system ---");
            string dest = targetRoot.TrimEnd('\\', '/');
            var failedPrograms = new List<string>();

            // Wimlib's extract creates ALL directories first, then writes file data.
            // If it hits a locked directory during the dir-creation pass it aborts before
            // writing anything — leaving empty folder skeletons everywhere.
            //
            // DISM mounts the WIM as a read-only virtual filesystem; robocopy then copies
            // file-by-file with /R:0 so locked files are individually skipped rather than
            // aborting the entire tree. Falls back to wimlib extract if DISM can't mount
            // (e.g. DISM incompatibility with solid WIM format).

            string mountDir = Path.Combine(Path.GetTempPath(), $"imgtool-mount-{Guid.NewGuid()}");
            Directory.CreateDirectory(mountDir);

            bool mounted = await TryMountWim(sourceWim, mountDir);
            if (mounted)
            {
                try   { await CopyWithRobocopy(mountDir, dest, failedPrograms); }
                finally
                {
                    await UnmountWim(mountDir);
                    try { Directory.Delete(mountDir); } catch { }
                }
            }
            else
            {
                try { Directory.Delete(mountDir); } catch { }
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("DISM mount unavailable — falling back to wimlib extract.");
                Console.WriteLine("Locked files may cause some program folders to be empty.");
                Console.ResetColor();
                await ExtractFileSystemWimlib(sourceWim, dest, failedPrograms);
            }

            PrintFailureSummary(failedPrograms);
        }

        private static async Task<bool> TryMountWim(string sourceWim, string mountDir)
        {
            Console.WriteLine($"Mounting WIM read-only via DISM (this may take a minute)...");

            var psi = new ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = $"/Mount-Wim /WimFile:\"{sourceWim}\" /index:1 /MountDir:\"{mountDir}\" /ReadOnly",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();
                // Drain both pipes to avoid hangs
                var outTask = Task.Run(() => process.StandardOutput.ReadToEnd());
                var errTask = Task.Run(() => process.StandardError.ReadToEnd());
                await process.WaitForExitAsync();
                await Task.WhenAll(outTask, errTask);

                if (process.ExitCode == 0)
                {
                    Console.WriteLine("WIM mounted successfully.");
                    return true;
                }

                string errText = (await errTask).Trim();
                Console.WriteLine($"DISM mount failed (exit {process.ExitCode}){(string.IsNullOrEmpty(errText) ? "." : $": {errText}")}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DISM not available: {ex.Message}");
                return false;
            }
        }

        private static async Task CopyWithRobocopy(string mountDir, string dest, List<string> failedPrograms)
        {
            foreach (string wimPath in FileSystemPaths)
            {
                string sourcePath = mountDir + wimPath;
                if (!Directory.Exists(sourcePath))
                {
                    Console.WriteLine($"\nSkipping {wimPath} — not found in image.");
                    continue;
                }

                Console.WriteLine($"\nCopying: {wimPath}");

                // Copy each top-level subdirectory separately so we know exactly which
                // program/user folders fail rather than just knowing the whole tree had issues.
                foreach (string sourceAppDir in Directory.GetDirectories(sourcePath))
                {
                    string appName = Path.GetFileName(sourceAppDir);
                    string destAppDir = Path.Combine(dest + wimPath, appName);
                    Directory.CreateDirectory(destAppDir);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "robocopy.exe",
                        // /E  — recursive including empty dirs
                        // /B  — backup mode (uses backup privileges, copies some otherwise-locked files)
                        // /XJ — skip junctions/symlinks (avoid following them into system dirs)
                        // /R:0 /W:0 — no retries; skip locked files immediately
                        // /NJH /NJS /NFL /NDL — suppress noisy output
                        Arguments = $"\"{sourceAppDir}\" \"{destAppDir}\" /E /B /XJ /R:0 /W:0 /NJH /NJS /NFL /NDL",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    using var robo = new Process { StartInfo = psi };
                    robo.Start();
                    await robo.WaitForExitAsync();

                    // Robocopy exit < 8: success (0=nothing to do, 1=copied, 2=extra, 4=mismatch)
                    // Robocopy exit >= 8: at least some files could not be copied
                    if (robo.ExitCode >= 8)
                        failedPrograms.Add($"{wimPath}\\{appName} (robocopy exit {robo.ExitCode})");
                }
            }
        }

        private static async Task UnmountWim(string mountDir)
        {
            Console.WriteLine("\nUnmounting WIM...");
            var psi = new ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = $"/Unmount-Wim /MountDir:\"{mountDir}\" /Discard",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();
                var outTask = Task.Run(() => process.StandardOutput.ReadToEnd());
                var errTask = Task.Run(() => process.StandardError.ReadToEnd());
                await process.WaitForExitAsync();
                await Task.WhenAll(outTask, errTask);
                Console.WriteLine(process.ExitCode == 0
                    ? "WIM unmounted."
                    : $"Warning: DISM unmount returned {process.ExitCode} — mount point may need manual cleanup: dism /Unmount-Wim /MountDir:\"{mountDir}\" /Discard");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not unmount WIM: {ex.Message}");
                Console.WriteLine($"  Manual cleanup: dism /Unmount-Wim /MountDir:\"{mountDir}\" /Discard");
            }
        }

        private async Task ExtractFileSystemWimlib(string sourceWim, string dest, List<string> failedPrograms)
        {
            foreach (string wimPath in FileSystemPaths)
            {
                Console.WriteLine($"\nExtracting: {wimPath}");
                string args = $"extract \"{sourceWim}\" 1 \"{wimPath}\" --dest-dir=\"{dest}\" --no-acls --tolerant";
                bool ok = await _processRunner.RunProcessAsync(_settings.WimlibPath, args, "WimLib Extract");
                if (!ok)
                    failedPrograms.Add(wimPath);
            }
        }

        private static void PrintFailureSummary(List<string> failedPrograms)
        {
            if (failedPrograms.Count == 0) return;

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n{failedPrograms.Count} director{(failedPrograms.Count == 1 ? "y" : "ies")} skipped (already installed/running on this system):");
            foreach (string p in failedPrograms)
                Console.WriteLine($"  {p}");
            Console.ResetColor();
        }

        private async Task MergeHklmRegistry(string sourceWim)
        {
            Console.WriteLine("\n--- Merging HKLM\\SOFTWARE registry ---");

            var tempDir = Path.Combine(Path.GetTempPath(), $"imgtool-reg-{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);

            try
            {
                string extractArgs =
                    $"extract \"{sourceWim}\" 1 \"\\Windows\\System32\\config\\SOFTWARE\" " +
                    $"--dest-dir=\"{tempDir}\" --no-acls";

                Console.WriteLine("Extracting HKLM SOFTWARE hive from WIM...");
                bool extracted = await _processRunner.RunProcessAsync(_settings.WimlibPath, extractArgs, "WimLib Extract");

                if (!extracted)
                {
                    Console.WriteLine("Warning: Could not extract SOFTWARE hive. Registry merge skipped.");
                    return;
                }

                // Wimlib may place the file directly in tempDir or preserve the full WIM path
                // structure (tempDir\Windows\System32\config\SOFTWARE) depending on version.
                // Search recursively so we find it either way.
                string? hivePath = Directory.GetFiles(tempDir, "SOFTWARE", SearchOption.AllDirectories)
                                            .FirstOrDefault();
                if (string.IsNullOrEmpty(hivePath))
                {
                    Console.WriteLine("Warning: SOFTWARE hive not found after extraction. Registry merge skipped.");
                    return;
                }

                string tempKey = $"HKLM\\{TempHiveKey}";
                Console.WriteLine($"Loading hive: {hivePath}");
                bool loaded = await _processRunner.RunProcessAsync(
                    RegExePath, $"load \"{tempKey}\" \"{hivePath}\"", "reg load");

                if (!loaded)
                {
                    Console.WriteLine("Warning: Could not load SOFTWARE hive. Registry merge skipped.");
                    return;
                }

                try
                {
                    MergeKeys();
                }
                finally
                {
                    // Force-close any open RegistryKey handles before unloading
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await _processRunner.RunProcessAsync(RegExePath, $"unload \"{tempKey}\"", "reg unload");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private void MergeKeys()
        {
            using var source = Registry.LocalMachine.OpenSubKey(TempHiveKey);
            using var dest = Registry.LocalMachine.OpenSubKey("SOFTWARE", writable: true);

            if (source == null || dest == null)
            {
                Console.WriteLine("Warning: Could not open registry keys for merging.");
                return;
            }

            // Third-party vendor keys (e.g. Adobe, Notepad++, etc.)
            foreach (string name in source.GetSubKeyNames())
            {
                if (SkipTopLevelKeys.Contains(name)) continue;
                Console.WriteLine($"  HKLM\\SOFTWARE\\{name}");
                try
                {
                    using var src = source.OpenSubKey(name);
                    using var dst = dest.CreateSubKey(name, writable: true);
                    if (src != null && dst != null) CopyKey(src, dst);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Skipped {name}: {ex.Message}");
                }
            }

            // Selected Microsoft subkeys needed for app recognition
            using var srcMs = source.OpenSubKey("Microsoft");
            using var dstMs = dest.OpenSubKey("Microsoft", writable: true);
            if (srcMs != null && dstMs != null)
            {
                foreach (string subPath in MicrosoftAppSubKeys)
                {
                    Console.WriteLine($"  HKLM\\SOFTWARE\\Microsoft\\{subPath}");
                    try
                    {
                        using var src = srcMs.OpenSubKey(subPath);
                        using var dst = dstMs.CreateSubKey(subPath, writable: true);
                        if (src != null && dst != null) CopyKey(src, dst);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Skipped Microsoft\\{subPath}: {ex.Message}");
                    }
                }
            }

            // WOW6432Node — 32-bit app registry on 64-bit Windows
            using var srcWow = source.OpenSubKey("WOW6432Node");
            using var dstWow = dest.OpenSubKey("WOW6432Node", writable: true);
            if (srcWow != null && dstWow != null)
            {
                foreach (string name in srcWow.GetSubKeyNames())
                {
                    if (name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    Console.WriteLine($"  HKLM\\SOFTWARE\\WOW6432Node\\{name}");
                    try
                    {
                        using var src = srcWow.OpenSubKey(name);
                        using var dst = dstWow.CreateSubKey(name, writable: true);
                        if (src != null && dst != null) CopyKey(src, dst);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Skipped WOW6432Node\\{name}: {ex.Message}");
                    }
                }
            }
        }

        private static void CopyKey(RegistryKey source, RegistryKey destination)
        {
            foreach (string name in source.GetValueNames())
            {
                try
                {
                    var value = source.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null) continue;
                    destination.SetValue(name, value, source.GetValueKind(name));
                }
                catch { }
            }

            foreach (string subName in source.GetSubKeyNames())
            {
                try
                {
                    using var srcSub = source.OpenSubKey(subName);
                    if (srcSub == null) continue;
                    using var dstSub = destination.CreateSubKey(subName, writable: true);
                    CopyKey(srcSub, dstSub);
                }
                catch { }
            }
        }
    }
}
