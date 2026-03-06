using System.Diagnostics;
using System.Text;

namespace ImagingTool.Helpers
{
    public class ProcessRunner : IProcessRunner
    {
        public async Task<bool> RunProcessAsync(string fileName, string arguments, string processName)
        {
            object consoleLock = new object();
            return await RunProcessWithProgressAsync(fileName, arguments, processName, line =>
            {
                lock (consoleLock) { Console.WriteLine(line); }
            });
        }

        public async Task<bool> RunProcessWithProgressAsync(
            string fileName, string arguments, string processName, Action<string> onLine)
        {
            Console.WriteLine($"\nExecuting {processName} command:");
            Console.WriteLine($"{fileName} {arguments}\n");

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using var process = new Process { StartInfo = psi };

                if (!process.Start())
                    throw new InvalidOperationException($"Failed to start process: {fileName}");

                // Read both streams as raw characters, splitting on \r and \n, so that
                // tools using \r-only progress lines (like wimlib) are delivered immediately.
                Task ReadRaw(StreamReader reader) => Task.Run(async () =>
                {
                    var sb = new StringBuilder();
                    var buf = new char[4096];
                    int n;
                    while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            char c = buf[i];
                            if (c == '\r' || c == '\n')
                            {
                                if (sb.Length > 0)
                                {
                                    onLine(sb.ToString());
                                    sb.Clear();
                                }
                            }
                            else
                            {
                                sb.Append(c);
                            }
                        }
                    }
                    if (sb.Length > 0)
                        onLine(sb.ToString());
                });

                var stdoutTask = ReadRaw(process.StandardOutput);
                var stderrTask = ReadRaw(process.StandardError);

                await process.WaitForExitAsync();
                await Task.WhenAll(stdoutTask, stderrTask);

                Console.WriteLine($"\n{processName} process finished.");

                if (process.ExitCode != 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"{processName} exited with error code: {process.ExitCode}.");
                    Console.ResetColor();
                    return false;
                }

                Console.WriteLine($"{processName} completed successfully (Exit Code: {process.ExitCode}).");
                return true;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n--- Error executing {processName} ---");
                Console.WriteLine($"Message: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }
    }
}
