namespace ImagingTool.Helpers
{
    public interface IProcessRunner
    {
        Task<bool> RunProcessAsync(string fileName, string arguments, string processName);
    }
}
