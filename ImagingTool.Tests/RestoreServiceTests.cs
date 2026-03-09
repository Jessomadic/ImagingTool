using ImagingTool.Helpers;
using ImagingTool.Services;
using Moq;
using Xunit;

namespace ImagingTool.Tests;

public class RestoreServiceTests
{
    private static AppSettings CreateSettings() => new AppSettings
    {
        WimlibSubDir = "wimlib",
        WimlibExeName = "wimlib-imagex.exe",
        WimlibDownloadUrl = "https://example.com/wimlib.zip",
        DotNetDownloadPageUrl = "https://example.com/dotnet",
        DotNetRuntimeInstallerUrl = "https://example.com/dotnet-installer.exe"
    };

    // Helper: sets up the mock so wimlib (RunProcessWithStderrAsync) returns the given result
    private static void SetupWimlib(Mock<IProcessRunner> mock, bool returns) =>
        mock.Setup(r => r.RunProcessWithStderrAsync(
                It.IsAny<string>(),
                It.Is<string>(a => a.Contains("apply")),
                It.IsAny<string>(),
                It.IsAny<Action<string>>()))
            .ReturnsAsync(returns);

    // Helper: sets up the mock so bcdboot (RunProcessAsync) returns the given result
    private static void SetupBcdboot(Mock<IProcessRunner> mock, bool returns) =>
        mock.Setup(r => r.RunProcessAsync("bcdboot.exe", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(returns);

    [Fact]
    public async Task ApplyWimImageAndConfigureBoot_WimlibSucceeds_RunsBcdboot()
    {
        var mockRunner = new Mock<IProcessRunner>();
        SetupWimlib(mockRunner, true);
        SetupBcdboot(mockRunner, true);

        var service = new RestoreService(CreateSettings(), mockRunner.Object);
        await service.ApplyWimImageAndConfigureBoot(@"D:\backup.wim", "E:");

        mockRunner.Verify(r => r.RunProcessWithStderrAsync(
            It.IsAny<string>(),
            It.Is<string>(a => a.Contains("apply")),
            "WimLib Apply",
            It.IsAny<Action<string>>()), Times.Once);

        mockRunner.Verify(r => r.RunProcessAsync(
            "bcdboot.exe", It.IsAny<string>(), "BCDBoot"), Times.Once);
    }

    [Fact]
    public async Task ApplyWimImageAndConfigureBoot_WimlibFails_ThrowsAndSkipsBcdboot()
    {
        var mockRunner = new Mock<IProcessRunner>();
        SetupWimlib(mockRunner, false);

        var service = new RestoreService(CreateSettings(), mockRunner.Object);

        await Assert.ThrowsAsync<Exception>(() =>
            service.ApplyWimImageAndConfigureBoot(@"D:\backup.wim", "E:"));

        mockRunner.Verify(r => r.RunProcessAsync(
            "bcdboot.exe", It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ApplyWimImageAndConfigureBoot_BcdbootFails_DoesNotThrow()
    {
        var mockRunner = new Mock<IProcessRunner>();
        SetupWimlib(mockRunner, true);
        SetupBcdboot(mockRunner, false);

        var service = new RestoreService(CreateSettings(), mockRunner.Object);

        // bcdboot failure warns but does not throw — files were restored, only boot config failed
        var exception = await Record.ExceptionAsync(() =>
            service.ApplyWimImageAndConfigureBoot(@"D:\backup.wim", "E:"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ApplyWimImageAndConfigureBoot_PassesCorrectSourceWimToWimlib()
    {
        var mockRunner = new Mock<IProcessRunner>();
        SetupWimlib(mockRunner, true);
        SetupBcdboot(mockRunner, true);

        var service = new RestoreService(CreateSettings(), mockRunner.Object);
        await service.ApplyWimImageAndConfigureBoot(@"D:\backup.wim", "E:");

        mockRunner.Verify(r => r.RunProcessWithStderrAsync(
            It.IsAny<string>(),
            It.Is<string>(a => a.Contains(@"D:\backup.wim")),
            "WimLib Apply",
            It.IsAny<Action<string>>()), Times.Once);
    }

    [Fact]
    public async Task ApplyWimImageAndConfigureBoot_PassesTargetDirectoryToWimlib()
    {
        var mockRunner = new Mock<IProcessRunner>();
        SetupWimlib(mockRunner, true);
        SetupBcdboot(mockRunner, true);

        var service = new RestoreService(CreateSettings(), mockRunner.Object);
        await service.ApplyWimImageAndConfigureBoot(@"D:\backup.wim", "E:");

        mockRunner.Verify(r => r.RunProcessWithStderrAsync(
            It.IsAny<string>(),
            It.Is<string>(a => a.Contains(@"E:\")),
            "WimLib Apply",
            It.IsAny<Action<string>>()), Times.Once);
    }

    [Fact]
    public async Task ApplyWimImageAndConfigureBoot_PassesWindowsFolderToBcdboot()
    {
        var mockRunner = new Mock<IProcessRunner>();
        SetupWimlib(mockRunner, true);
        SetupBcdboot(mockRunner, true);

        var service = new RestoreService(CreateSettings(), mockRunner.Object);
        await service.ApplyWimImageAndConfigureBoot(@"D:\backup.wim", "E:");

        mockRunner.Verify(r => r.RunProcessAsync(
            "bcdboot.exe",
            It.Is<string>(a => a.Contains(@"E:\") && a.Contains("Windows")),
            "BCDBoot"), Times.Once);
    }
}
