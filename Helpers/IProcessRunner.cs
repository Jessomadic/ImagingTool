namespace ImagingTool.Helpers
{
    public interface IProcessRunner
    {
        /// <summary>Runs a process, printing all output to the console. Returns true on exit code 0.</summary>
        Task<bool> RunProcessAsync(string fileName, string arguments, string processName);

        /// <summary>
        /// Runs a process and calls <paramref name="onLine"/> for every line received from
        /// stdout or stderr (splitting on both \r and \n so \r-only progress lines are delivered
        /// immediately). Returns true on exit code 0.
        /// </summary>
        Task<bool> RunProcessWithProgressAsync(string fileName, string arguments, string processName, Action<string> onLine);
    }
}
