using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ImagingTool.Services
{
    public class RequirementsService
    {
        private readonly AppSettings _settings;

        public RequirementsService(AppSettings settings)
        {
            _settings = settings;
        }

        public async Task InitializeRequirements()
        {
            Console.WriteLine("\nChecking requirements...");

            if (!await CheckDotNetRuntime())
            {
                MessageBox.Show(
                    $"Failed requirement: .NET {_settings.DotNetRequiredVersion} Runtime is missing or incompatible.\n" +
                    $"Please install it manually from:\n{_settings.DotNetDownloadPageUrl}",
                    "Requirement Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new InvalidOperationException($"Failed requirement: .NET {_settings.DotNetRequiredVersion} Runtime.");
            }

            Console.WriteLine($".NET {_settings.DotNetRequiredVersion} Runtime check passed.");

            if (!await CheckWimLib())
            {
                MessageBox.Show(
                    $"Failed requirement: WimLib was not found or could not be installed.\n" +
                    $"Please ensure '{_settings.WimlibPath}' exists or try installing manually from {_settings.WimlibDownloadUrl}",
                    "Requirement Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw new InvalidOperationException("Failed requirement: WimLib installation.");
            }

            Console.WriteLine("WimLib check passed.");
            Console.WriteLine("Requirements met.");
        }

        private async Task<bool> CheckDotNetRuntime()
        {
            Console.WriteLine($"Checking for .NET Runtime {_settings.DotNetRequiredVersion} or later...");

            if (IsDotNetRuntimeInstalled(_settings.DotNetRequiredVersion))
                return true;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Required .NET {_settings.DotNetRequiredVersion} Runtime not found. Attempting automatic install...");
            Console.ResetColor();

            bool installed = await TryInstallDotNetRuntime();
            if (!installed)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Automatic install failed. Please install .NET {_settings.DotNetRequiredVersion} Runtime manually:");
                Console.WriteLine($"  {_settings.DotNetDownloadPageUrl}");
                Console.ResetColor();
                return false;
            }

            // Verify the runtime is now detectable
            if (!IsDotNetRuntimeInstalled(_settings.DotNetRequiredVersion))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Runtime installed but not yet detectable — a reboot may be required.");
                Console.ResetColor();
                // Return true anyway; if the installer succeeded the runtime is there even if
                // dotnet CLI isn't in PATH yet.
            }

            return true;
        }

        private async Task<bool> TryInstallDotNetRuntime()
        {
            if (string.IsNullOrWhiteSpace(_settings.DotNetRuntimeInstallerUrl))
            {
                Console.WriteLine("No installer URL configured (DotNetRuntimeInstallerUrl is empty).");
                return false;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"dotnet-install-{Guid.NewGuid()}");
            var installerPath = Path.Combine(tempDir, "dotnet-runtime-installer.exe");

            try
            {
                Directory.CreateDirectory(tempDir);

                Console.WriteLine($"Downloading .NET Runtime installer from:");
                Console.WriteLine($"  {_settings.DotNetRuntimeInstallerUrl}");

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "ImagingTool/1.0");
                    var bytes = await client.GetByteArrayAsync(_settings.DotNetRuntimeInstallerUrl);
                    await File.WriteAllBytesAsync(installerPath, bytes);
                }

                Console.WriteLine("Download complete. Running installer silently...");

                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Console.WriteLine("Failed to start installer process.");
                    return false;
                }

                await process.WaitForExitAsync();

                // Exit 0 = success, 3010 = success but reboot required
                if (process.ExitCode == 0 || process.ExitCode == 3010)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(process.ExitCode == 3010
                        ? $".NET {_settings.DotNetRequiredVersion} Runtime installed — a reboot is required to complete setup."
                        : $".NET {_settings.DotNetRequiredVersion} Runtime installed successfully.");
                    Console.ResetColor();
                    return true;
                }

                Console.WriteLine($"Installer exited with code {process.ExitCode}.");
                return false;
            }
            catch (HttpRequestException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to download installer: {ex.Message}");
                Console.ResetColor();
                return false;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to install .NET Runtime: {ex.Message}");
                Console.ResetColor();
                return false;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static bool IsDotNetRuntimeInstalled(string requiredVersionString)
        {
            if (!Version.TryParse(requiredVersionString, out var requiredVersion))
            {
                Console.WriteLine($"Warning: Invalid required .NET version format in config: {requiredVersionString}");
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Console.WriteLine("Warning: Failed to start 'dotnet' process.");
                    return false;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine("Info: 'dotnet --list-runtimes' failed. Assuming .NET is not installed or accessible.");
                    return false;
                }

                var runtimeRegex = new Regex(@"^Microsoft\.NETCore\.App\s+(\d+\.\d+\.\d+)", RegexOptions.Multiline);
                foreach (Match match in runtimeRegex.Matches(output).Cast<Match>())
                {
                    if (match.Groups.Count > 1 && Version.TryParse(match.Groups[1].Value, out var installedVersion))
                    {
                        bool compatible = installedVersion.Major > requiredVersion.Major ||
                            (installedVersion.Major == requiredVersion.Major && installedVersion.Minor >= requiredVersion.Minor);

                        if (compatible)
                        {
                            Console.WriteLine($"Found compatible .NET Runtime: {installedVersion}");
                            return true;
                        }
                    }
                }

                Console.WriteLine("No compatible .NET Runtime version found.");
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine("Info: 'dotnet' command not found. Assuming .NET is not installed or not in PATH.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Error checking .NET runtime: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> CheckWimLib()
        {
            Console.WriteLine($"Checking for WimLib at: {_settings.WimlibPath}");

            if (!File.Exists(_settings.WimlibPath))
            {
                Console.WriteLine("WimLib not found. Attempting to download and install...");
                return await InstallWimLib();
            }

            return true;
        }

        private async Task<bool> InstallWimLib()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"wimlib-temp-{Guid.NewGuid()}");
            var zipPath = Path.Combine(tempDir, "wimlib.zip");

            Console.WriteLine($"Downloading WimLib from: {_settings.WimlibDownloadUrl}");
            Console.WriteLine($"Temporary download location: {zipPath}");

            try
            {
                Directory.CreateDirectory(tempDir);

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "ImagingTool/1.0");
                    Console.WriteLine("Starting download...");
                    var zipBytes = await client.GetByteArrayAsync(_settings.WimlibDownloadUrl);
                    Console.WriteLine("Download complete. Writing to disk...");
                    await File.WriteAllBytesAsync(zipPath, zipBytes);
                }

                // File handle is fully released before extraction begins
                Console.WriteLine($"Extracting WimLib archive to: {tempDir}");
                ZipFile.ExtractToDirectory(zipPath, tempDir, true);

                string? foundExePath = Directory.GetFiles(tempDir, _settings.WimlibExeName, SearchOption.AllDirectories).FirstOrDefault();
                if (string.IsNullOrEmpty(foundExePath))
                    throw new FileNotFoundException($"'{_settings.WimlibExeName}' not found within the downloaded archive.");

                string? sourceBinDir = Path.GetDirectoryName(foundExePath);
                if (sourceBinDir == null)
                    throw new DirectoryNotFoundException("Could not determine the source directory containing wimlib binaries.");

                Console.WriteLine($"Copying WimLib binaries from '{sourceBinDir}' to: {_settings.WimlibDir}");
                Directory.CreateDirectory(_settings.WimlibDir);

                int filesCopied = 0;
                foreach (var file in Directory.GetFiles(sourceBinDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension == ".exe" || extension == ".dll")
                    {
                        var destPath = Path.Combine(_settings.WimlibDir, Path.GetFileName(file));
                        File.Copy(file, destPath, true);
                        Console.WriteLine($"  Copied: {Path.GetFileName(file)}");
                        filesCopied++;
                    }
                }

                if (filesCopied == 0)
                    throw new InvalidOperationException($"No .exe or .dll files were found to copy from '{sourceBinDir}'.");

                if (!File.Exists(_settings.WimlibPath))
                    throw new FileNotFoundException($"WimLib installation failed. Expected executable not found at: {_settings.WimlibPath}");

                Console.WriteLine("WimLib installation successful.");
                return true;
            }
            catch (HttpRequestException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error downloading WimLib: {ex.Message}");
                Console.WriteLine($"Please check the URL ({_settings.WimlibDownloadUrl}) in appsettings.json and your internet connection.");
                Console.ResetColor();
                return false;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to install WimLib: {ex.Message}");
                Console.ResetColor();

                if (Directory.Exists(_settings.WimlibDir))
                {
                    try { Directory.Delete(_settings.WimlibDir, true); }
                    catch (IOException ioEx) { Console.WriteLine($"Warning: Could not clean up WimLib directory: {ioEx.Message}"); }
                }

                return false;
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); }
                    catch (IOException ioEx) { Console.WriteLine($"Warning: Could not clean up temp directory: {ioEx.Message}"); }
                }
            }
        }
    }
}
