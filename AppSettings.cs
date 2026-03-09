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

        /// <summary>Max wimlib worker threads. 0 = use processor count (capped at 16).</summary>
        public int WimThreadCount { get; set; } = 0;

        /// <summary>Solid block size in MiB for solid-mode compression.</summary>
        public int WimSolidChunkSizeMiB { get; set; } = 64;

        /// <summary>Embed SHA-1 integrity data in the WIM (--check). Disable for faster local backups.</summary>
        public bool WimEnableIntegrityCheck { get; set; } = true;

        /// <summary>Continue backup when individual files can't be read (--continue). Prevents one bad file aborting the whole run.</summary>
        public bool IgnoreFileReadErrors { get; set; } = true;

        public string WimlibDir => Path.Combine(AppContext.BaseDirectory, WimlibSubDir);
        public string WimlibPath => Path.Combine(WimlibDir, WimlibExeName);
    }
}
