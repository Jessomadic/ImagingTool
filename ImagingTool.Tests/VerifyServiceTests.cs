using ImagingTool.Helpers;
using ImagingTool.Services;
using Moq;
using Xunit;

namespace ImagingTool.Tests;

public class VerifyServiceTests
{
    private static AppSettings CreateSettings() => new AppSettings
    {
        WimlibSubDir = "wimlib",
        WimlibExeName = "wimlib-imagex.exe",
        WimlibDownloadUrl = "https://example.com/wimlib.zip",
        DotNetDownloadPageUrl = "https://example.com/dotnet",
        DotNetRuntimeInstallerUrl = "https://example.com/dotnet-installer.exe"
    };

    [Fact]
    public async Task VerifyWimFile_FileNotFound_ThrowsFileNotFoundException()
    {
        var mockRunner = new Mock<IProcessRunner>();
        var service = new VerifyService(CreateSettings(), mockRunner.Object);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.VerifyWimFile(@"C:\nonexistent\backup.wim"));

        // Process must not be invoked when file doesn't exist
        mockRunner.Verify(r => r.RunProcessAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task VerifyWimFile_WimlibSucceeds_CompletesWithoutException()
    {
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner
            .Setup(r => r.RunProcessAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var service = new VerifyService(CreateSettings(), mockRunner.Object);
        var tempWim = CreateTempFile();
        try
        {
            var exception = await Record.ExceptionAsync(() => service.VerifyWimFile(tempWim));
            Assert.Null(exception);
        }
        finally
        {
            File.Delete(tempWim);
        }
    }

    [Fact]
    public async Task VerifyWimFile_WimlibFails_ThrowsException()
    {
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner
            .Setup(r => r.RunProcessAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var service = new VerifyService(CreateSettings(), mockRunner.Object);
        var tempWim = CreateTempFile();
        try
        {
            await Assert.ThrowsAsync<Exception>(() => service.VerifyWimFile(tempWim));
        }
        finally
        {
            File.Delete(tempWim);
        }
    }

    [Fact]
    public async Task VerifyWimFile_InvokesWimlibWithVerifyCommand()
    {
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner
            .Setup(r => r.RunProcessAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var service = new VerifyService(CreateSettings(), mockRunner.Object);
        var tempWim = CreateTempFile();
        try
        {
            await service.VerifyWimFile(tempWim);

            mockRunner.Verify(r => r.RunProcessAsync(
                It.IsAny<string>(),
                It.Is<string>(a => a.Contains("verify")),
                "WimLib Verify"), Times.Once);
        }
        finally
        {
            File.Delete(tempWim);
        }
    }

    [Fact]
    public async Task VerifyWimFile_PassesCorrectWimPathToProcess()
    {
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner
            .Setup(r => r.RunProcessAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var service = new VerifyService(CreateSettings(), mockRunner.Object);
        var tempWim = CreateTempFile();
        try
        {
            await service.VerifyWimFile(tempWim);

            mockRunner.Verify(r => r.RunProcessAsync(
                It.IsAny<string>(),
                It.Is<string>(a => a.Contains(tempWim)),
                "WimLib Verify"), Times.Once);
        }
        finally
        {
            File.Delete(tempWim);
        }
    }

    private static string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.wim");
        File.WriteAllText(path, "dummy");
        return path;
    }
}
