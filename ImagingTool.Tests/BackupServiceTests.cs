using ImagingTool.Services;
using Xunit;

namespace ImagingTool.Tests;

public class BackupServiceTests
{
    // --- Compression Level ---

    [Theory]
    [InlineData("none", "none")]
    [InlineData("None", "none")]
    [InlineData("NONE", "none")]
    [InlineData("maximum", "lzx")]
    [InlineData("Maximum", "lzx")]
    [InlineData("MAXIMUM", "lzx")]
    [InlineData("fast", "fast")]
    [InlineData("Fast", "fast")]
    [InlineData("FAST", "fast")]
    [InlineData("unknown", "fast")]
    [InlineData("", "fast")]
    [InlineData(null, "fast")]
    public void ResolveCompressionLevel_ReturnsExpectedArg(string? input, string expectedArg)
    {
        var (arg, _) = BackupService.ResolveCompressionLevel(input);
        Assert.Equal(expectedArg, arg);
    }

    [Theory]
    [InlineData("none", "None (Fastest, Largest File)")]
    [InlineData("maximum", "Maximum (Slowest, Smallest File)")]
    [InlineData("fast", "Fast (Balanced)")]
    [InlineData("unknown", "Fast (Balanced)")]
    [InlineData(null, "Fast (Balanced)")]
    public void ResolveCompressionLevel_ReturnsExpectedDisplay(string? input, string expectedDisplay)
    {
        var (_, display) = BackupService.ResolveCompressionLevel(input);
        Assert.Equal(expectedDisplay, display);
    }

    // --- Error Detection ---

    [Theory]
    [InlineData("ERROR: could not open file")]
    [InlineData("error: lowercase error")]
    [InlineData("Failed to read sector 42")]
    [InlineData("FAILED")]
    [InlineData("Cannot open stream for writing")]
    [InlineData("cannot find volume")]
    public void IsWimlibError_ErrorLine_ReturnsTrue(string line)
    {
        Assert.True(BackupService.IsWimlibError(line));
    }

    [Theory]
    [InlineData(@"Adding file: [C:\Windows\notepad.exe]")]
    [InlineData("1.23 GiB / 50.00 GiB (10% done)")]
    [InlineData(@"Scanning C:\Windows...")]
    [InlineData("Using 8 threads")]
    [InlineData("WIM header written.")]
    [InlineData("")]
    public void IsWimlibError_NormalLine_ReturnsFalse(string line)
    {
        Assert.False(BackupService.IsWimlibError(line));
    }

    [Fact]
    public void IsWimlibError_MftMissingParentInode_ReturnsTrue()
    {
        // This line IS matched by IsWimlibError (it contains "Failed"), but
        // the caller checks it separately to treat as a warning rather than error.
        // The detection itself should still return true.
        string mftLine = "Parent inode 12345 was missing from the MFT listing: Failed";
        Assert.True(BackupService.IsWimlibError(mftLine));
    }

    // --- Exclusion Config ---

    [Fact]
    public async Task WriteExclusionConfig_CreatesFile()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"test-excl-{Guid.NewGuid()}.txt");
        try
        {
            await BackupService.WriteExclusionConfig(tempPath);
            Assert.True(File.Exists(tempPath));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task WriteExclusionConfig_ContainsExclusionListHeader()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"test-excl-{Guid.NewGuid()}.txt");
        try
        {
            await BackupService.WriteExclusionConfig(tempPath);
            string[] lines = await File.ReadAllLinesAsync(tempPath);
            Assert.Contains("[ExclusionList]", lines);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Theory]
    [InlineData(@"\pagefile.sys")]
    [InlineData(@"\swapfile.sys")]
    [InlineData(@"\hiberfil.sys")]
    [InlineData(@"\System Volume Information")]
    [InlineData(@"\$Recycle.Bin")]
    public async Task WriteExclusionConfig_ContainsExpectedExclusions(string expectedEntry)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"test-excl-{Guid.NewGuid()}.txt");
        try
        {
            await BackupService.WriteExclusionConfig(tempPath);
            string[] lines = await File.ReadAllLinesAsync(tempPath);
            Assert.Contains(expectedEntry, lines);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
