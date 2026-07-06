using bzTorrentClient.Avalonia;

namespace bzTorrentClient.Avalonia.Tests;

public class ByteFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(634, "634 B")]
    [InlineData(9000, "9000 B")]
    [InlineData(-5, "0 B")]
    public void Bytes_BelowStepUpThreshold_StaysInBytes(double bytes, string expected)
    {
        Assert.Equal(expected, ByteFormat.Bytes(bytes));
    }

    [Theory]
    [InlineData(1024 * 10, "10 KB")]
    [InlineData(38690, "37.8 KB")]
    public void Bytes_AtOrAboveStepUpThreshold_StepsUpToNextUnit(double bytes, string expected)
    {
        Assert.Equal(expected, ByteFormat.Bytes(bytes));
    }

    [Fact]
    public void Bytes_JustBelowStepUpThreshold_DoesNotStepUp()
    {
        // 1024*10 - 1 bytes is still "a lot of bytes" rather than "10 KB", by design —
        // only step up once the value would read as ten or more of the next unit.
        Assert.Equal("10239 B", ByteFormat.Bytes(1024 * 10 - 1));
    }

    [Fact]
    public void Bytes_LargeValue_StepsUpMultipleUnits()
    {
        Assert.Equal("19.1 MB", ByteFormat.Bytes(20_000_000));
    }

    [Fact]
    public void Bytes_AtTopUnit_DoesNotStepUpFurther()
    {
        var oneTerabyte = Math.Pow(1024, 4);
        Assert.Equal("1024 TB", ByteFormat.Bytes(oneTerabyte * 1024));
    }

    [Fact]
    public void Rate_AppendsPerSecondSuffix()
    {
        Assert.Equal("9000 B/s", ByteFormat.Rate(9000));
        Assert.Equal("37.8 KB/s", ByteFormat.Rate(38690));
    }
}
