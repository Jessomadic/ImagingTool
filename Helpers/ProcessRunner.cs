using System.Diagnostics;
using System.Text;

namespace ImagingTool.Helpers
{
    public class ProcessRunner : IProcessRunner
    {
        public async Task<bool> RunProcessAsync(string fileName, string arguments, string processName)
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

            object consoleLock = new object();
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var outputTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) { outputTcs.TrySetResult(true); return; }
                lock (consoleLock) { Console.WriteLine($"[{processName} STDOUT] {e.Data}"); }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) { errorTcs.TrySetResult(true); return; }
                lock (consoleLock) { Console.WriteLine($"[{processName} STDERR] {e.Data}"); }
            };

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException($"Failed to start process: {fileName}");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();
                await Task.WhenAll(outputTcs.Task, errorTcs.Task);

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
