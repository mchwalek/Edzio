using Edzio.Core.Transfer;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

public class ByteFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void Format_ReturnsHumanReadableString(long bytes, string expected)
    {
        ByteFormatter.Format(bytes).Should().Be(expected);
    }

    [Fact]
    public void Format_NegativeBytes_ClampsToZero()
    {
        ByteFormatter.Format(-100).Should().Be("0 B");
    }

    [Fact]
    public void FormatRate_ZeroOrNegative_ReturnsZeroBytesPerSecond()
    {
        ByteFormatter.FormatRate(0).Should().Be("0 B/s");
        ByteFormatter.FormatRate(-5).Should().Be("0 B/s");
    }

    [Fact]
    public void FormatRate_PositiveRate_AppendsPerSecondSuffix()
    {
        ByteFormatter.FormatRate(2_400_000).Should().Be("2.3 MB/s");
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(12, "12s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1:00")]
    [InlineData(92, "1:32")]
    public void FormatDuration_ReturnsHumanReadableString(double seconds, string expected)
    {
        ByteFormatter.FormatDuration(seconds).Should().Be(expected);
    }
}
