using System.Diagnostics;
using System.Security.Principal;

namespace ImagingTool.Helpers
{
    public static class VolumeHelper
    {
        public static bool IsRunningAsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not determine administrator status: {ex.Message}");
                return false;
            }
        }

        public static bool IsVolumeDirty(string driveLetter)
        {
            if (string.IsNullOrEmpty(driveLetter) || !driveLetter.EndsWith(':'))
            {
                Console.WriteLine($"Warning: Invalid drive letter format for dirty check: {driveLetter}");
                return false;
            }

            Console.WriteLine($"Checking dirty bit for volume {driveLetter}...");

            var psi = new ProcessStartInfo
            {
                FileName = "fsutil.exe",
                Arguments = $"dirty query {driveLetter}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                {
                    Console.WriteLine("Warning: Failed to start fsutil process.");
                    return false;
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    Console.WriteLine("Warning: fsutil process timed out. Assuming volume is not dirty.");
                    try { process.Kill(); } catch { /* ignore */ }
                    return false;
                }

                string output = outputTask.Result;
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    Console.WriteLine($"fsutil output: {output.Trim()}");
                    if (output.Contains(" is Dirty", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (output.Contains(" is NOT Dirty", StringComparison.OrdinalIgnoreCase))
                        return false;
                    Console.WriteLine("Warning: fsutil output format not recognized.");
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

            return false;
        }

        public static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Length <= maxLength) return value;
            return "..." + value.Substring(value.Length - maxLength + 3);
        }
    }
}
