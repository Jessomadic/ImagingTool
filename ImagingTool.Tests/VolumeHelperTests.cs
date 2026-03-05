using ImagingTool.Helpers;
using Xunit;

namespace ImagingTool.Tests;

public class VolumeHelperTests
{
    [Fact]
    public void Truncate_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, VolumeHelper.Truncate("", 10));
    }

    [Fact]
    public void Truncate_NullString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, VolumeHelper.Truncate(null!, 10));
    }

    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        Assert.Equal("hello", VolumeHelper.Truncate("hello", 10));
    }

    [Fact]
    public void Truncate_ExactLength_ReturnsUnchanged()
    {
        Assert.Equal("hello", VolumeHelper.Truncate("hello", 5));
    }

    [Fact]
    public void Truncate_LongString_StartsWithEllipsis()
    {
        string result = VolumeHelper.Truncate(@"C:\Windows\System32\drivers\etc\hosts", 20);
        Assert.StartsWith("...", result);
    }

    [Fact]
    public void Truncate_LongString_RespectsMaxLength()
    {
        string result = VolumeHelper.Truncate(@"C:\Windows\System32\drivers\etc\hosts", 20);
        Assert.Equal(20, result.Length);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(30)]
    public void Truncate_LongString_NeverExceedsMaxLength(int maxLength)
    {
        string longPath = @"C:\This\Is\A\Very\Long\Path\That\Exceeds\Any\Max\Length\We\Might\Set";
        string result = VolumeHelper.Truncate(longPath, maxLength);
        Assert.True(result.Length <= maxLength);
    }
}
