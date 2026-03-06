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

            foreach (string wimPath in FileSystemPaths)
            {
                Console.WriteLine($"\nExtracting: {wimPath}");
                string args = $"extract \"{sourceWim}\" 1 \"{wimPath}\" --dest-dir=\"{dest}\" --no-acls";
                bool ok = await _processRunner.RunProcessAsync(_settings.WimlibPath, args, "WimLib Extract");
                if (!ok)
                    Console.WriteLine($"  Warning: Some files in {wimPath} could not be extracted.");
            }
        }

        private async Task MergeHklmRegistry(string sourceWim)
        {
            Console.WriteLine("\n--- Merging HKLM\\SOFTWARE registry ---");

            var tempDir = Path.Combine(Path.GetTempPath(), $"imgtool-reg-{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Wimlib preserves the full path structure, so SOFTWARE ends up at:
                // <tempDir>\Windows\System32\config\SOFTWARE
                string extractArgs =
                    $"extract \"{sourceWim}\" 1 \"\\Windows\\System32\\config\\SOFTWARE\" " +
                    $"--dest-dir=\"{tempDir}\" --no-acls";

                Console.WriteLine("Extracting HKLM SOFTWARE hive from WIM...");
                bool extracted = await _processRunner.RunProcessAsync(_settings.WimlibPath, extractArgs, "WimLib Extract");

                string hivePath = Path.Combine(tempDir, "Windows", "System32", "config", "SOFTWARE");
                if (!extracted || !File.Exists(hivePath))
                {
                    Console.WriteLine("Warning: Could not extract SOFTWARE hive. Registry merge skipped.");
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
