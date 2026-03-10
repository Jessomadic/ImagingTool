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

        // All file system paths (full extract)
        private static readonly string[] FileSystemPaths =
        {
            @"\Users",
            @"\Program Files",
            @"\Program Files (x86)",
            @"\ProgramData",
        };

        // Software-only paths (no user profiles)
        private static readonly string[] SoftwarePaths =
        {
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
            Console.WriteLine("\n--- Data Extraction (Full) ---");
            Console.WriteLine($"Source WIM : {sourceWim}");
            Console.WriteLine($"Target Root: {targetRoot}");

            await ExtractFileSystem(sourceWim, targetRoot, FileSystemPaths);
            await MergeHklmRegistry(sourceWim);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nExtraction complete.");
            Console.ResetColor();
            Console.WriteLine("Note: Per-user settings (HKCU) are stored in NTUSER.DAT inside");
            Console.WriteLine("      each extracted user folder. They take effect after");
            Console.WriteLine("      logging out and back in with the matching username.");
        }

        public async Task PerformSoftwareExtraction(string sourceWim, string targetRoot)
        {
            Console.WriteLine("\n--- Software Extraction ---");
            Console.WriteLine($"Source WIM : {sourceWim}");
            Console.WriteLine($"Target Root: {targetRoot}");

            await ExtractFileSystem(sourceWim, targetRoot, SoftwarePaths);
            await MergeHklmRegistry(sourceWim);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nSoftware extraction complete.");
            Console.ResetColor();
        }

        private async Task ExtractFileSystem(string sourceWim, string targetRoot, string[] paths)
        {
            Console.WriteLine("\n--- Extracting file system ---");
            string dest = targetRoot.TrimEnd('\\', '/');
            var skipped = new List<string>();

            foreach (string wimPath in paths)
            {
                Console.WriteLine($"\nExtracting: {wimPath}");
                string args = $"extract \"{sourceWim}\" 1 \"{wimPath}\" --dest-dir=\"{dest}\" --no-acls --no-attributes";
                bool ok = await _processRunner.RunProcessAsync(_settings.WimlibPath, args, "WimLib Extract");
                if (ok) continue;

                // Wimlib creates the full directory skeleton before writing any file data.
                // A single locked directory in that first pass aborts everything, leaving
                // empty folders. The skeleton is already on disk though, so we can use it:
                // enumerate each top-level subdirectory and retry them one at a time.
                // Locked ones (already installed/running) fail fast and get skipped;
                // everything else extracts normally.
                string destPath = dest + wimPath;
                if (!Directory.Exists(destPath)) continue;

                Console.WriteLine($"  Retrying {wimPath} directory-by-directory...");
                foreach (string subDir in Directory.GetDirectories(destPath))
                {
                    string name = Path.GetFileName(subDir);
                    Console.Write($"    {name}... ");

                    var psi = new ProcessStartInfo
                    {
                        FileName = _settings.WimlibPath,
                        // dest-dir must be the parent folder (e.g. C:\Program Files) so wimlib
                        // places the app directory there directly. Using just "C:" would drop
                        // the path structure and put everything at the root.
                        Arguments = $"extract \"{sourceWim}\" 1 \"{wimPath}\\{name}\" --dest-dir=\"{dest + wimPath}\" --no-acls --no-attributes",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    using var proc = new Process { StartInfo = psi };
                    proc.Start();
                    var drainOut = Task.Run(() => proc.StandardOutput.ReadToEnd());
                    var drainErr = Task.Run(() => proc.StandardError.ReadToEnd());
                    await proc.WaitForExitAsync();
                    await Task.WhenAll(drainOut, drainErr);

                    if (proc.ExitCode == 0)
                    {
                        Console.WriteLine("ok");
                    }
                    else
                    {
                        // Only call it "already installed" if the directory actually has files.
                        // An empty folder is just a skeleton wimlib created in the first pass.
                        // Use a shallow single-level check — fast even on slow drives (OPT-5).
                        bool hasFiles = Directory.EnumerateFileSystemEntries(subDir).Any();
                        if (hasFiles)
                        {
                            Console.WriteLine("skipped (already installed)");
                            skipped.Add($"{wimPath}\\{name}");
                        }
                        else
                        {
                            Console.WriteLine("FAILED (could not extract — check permissions)");
                        }
                    }
                }
            }

            if (skipped.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n{skipped.Count} director{(skipped.Count == 1 ? "y" : "ies")} skipped (already installed/running):");
                foreach (string s in skipped)
                    Console.WriteLine($"  {s}");
                Console.ResetColor();
            }
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

                // Also extract the transaction log files in one batched call (OPT-2).
                // reg.exe load needs these present when the hive was captured live (dirty bit set).
                // Failures are ignored — logs may not exist in the WIM if cleanly committed.
                // --nullglob silently skips log files that don't exist in the WIM
                // (they may not be captured if the hive was cleanly committed at snapshot time).
                string logBatchArgs =
                    $"extract \"{sourceWim}\" 1 " +
                    $"\"\\Windows\\System32\\config\\SOFTWARE.LOG\" " +
                    $"\"\\Windows\\System32\\config\\SOFTWARE.LOG1\" " +
                    $"\"\\Windows\\System32\\config\\SOFTWARE.LOG2\" " +
                    $"--dest-dir=\"{tempDir}\" --no-acls --nullglob";
                await _processRunner.RunProcessAsync(_settings.WimlibPath, logBatchArgs, "WimLib Extract");

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
                    await MergeKeys(tempKey);
                }
                finally
                {
                    // All RegistryKey handles opened in MergeKeys are disposed via using-statements
                    // before we reach here, so no GC.Collect needed (OPT-15).
                    await _processRunner.RunProcessAsync(RegExePath, $"unload \"{tempKey}\"", "reg unload");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // OPT-14: Replace recursive .NET Registry API (thousands of individual syscalls) with
        // reg.exe copy, which does a native bulk subtree copy in a single process call.
        // We still use the Registry API to enumerate which keys to copy — only the actual
        // data transfer moves to reg.exe copy.
        private async Task MergeKeys(string tempKey)
        {
            using var source = Registry.LocalMachine.OpenSubKey(TempHiveKey);
            if (source == null)
            {
                Console.WriteLine("Warning: Could not open loaded hive for enumeration.");
                return;
            }

            // Third-party vendor keys (e.g. Adobe, Notepad++, etc.)
            foreach (string name in source.GetSubKeyNames())
            {
                if (SkipTopLevelKeys.Contains(name)) continue;
                Console.WriteLine($"  HKLM\\SOFTWARE\\{name}");
                await RegCopy($"{tempKey}\\{name}", $"HKLM\\SOFTWARE\\{name}");
            }

            // Selected Microsoft subkeys needed for app recognition
            foreach (string subPath in MicrosoftAppSubKeys)
            {
                Console.WriteLine($"  HKLM\\SOFTWARE\\Microsoft\\{subPath}");
                await RegCopy($"{tempKey}\\Microsoft\\{subPath}", $"HKLM\\SOFTWARE\\Microsoft\\{subPath}");
            }

            // WOW6432Node — 32-bit app registry on 64-bit Windows
            using var srcWow = source.OpenSubKey("WOW6432Node");
            if (srcWow != null)
            {
                foreach (string name in srcWow.GetSubKeyNames())
                {
                    if (name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    Console.WriteLine($"  HKLM\\SOFTWARE\\WOW6432Node\\{name}");
                    await RegCopy($"{tempKey}\\WOW6432Node\\{name}", $"HKLM\\SOFTWARE\\WOW6432Node\\{name}");
                }
            }
        }

        private async Task RegCopy(string src, string dest)
        {
            // /s = copy all subkeys recursively, /f = force overwrite without prompting
            await _processRunner.RunProcessAsync(
                RegExePath, $"copy \"{src}\" \"{dest}\" /s /f", "reg copy");
        }
    }
}
