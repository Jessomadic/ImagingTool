namespace ImagingTool
{
    public class AppSettings
    {
        public string WimlibSubDir { get; set; } = "wimlib";
        public string WimlibExeName { get; set; } = "wimlib-imagex.exe";
        public string WimlibDownloadUrl { get; set; } = "";
        public string DotNetRequiredVersion { get; set; } = "9.0";
        public string DotNetDownloadPageUrl { get; set; } = "";
        public string DotNetRuntimeInstallerUrl { get; set; } = "";
        public string WimCompressionLevel { get; set; } = "Fast";

        public string WimlibDir => Path.Combine(AppContext.BaseDirectory, WimlibSubDir);
        public string WimlibPath => Path.Combine(WimlibDir, WimlibExeName);
    }
}
