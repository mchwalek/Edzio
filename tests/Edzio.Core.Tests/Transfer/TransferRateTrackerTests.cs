using Edzio.Core.Transfer;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

public class TransferRateTrackerTests
{
    [Fact]
    public void Sample_FirstCall_ReturnsZeroRateAndNullEta()
    {
        var tracker = new TransferRateTracker();
        var start = DateTimeOffset.UtcNow;

        var snapshot = tracker.Sample(bytesSoFar: 0, totalBytes: 1000, start);

        snapshot.BytesPerSecond.Should().Be(0);
        snapshot.EtaSeconds.Should().BeNull();
    }

    [Fact]
    public void Sample_SecondCallOneSecondLater_ComputesRateFromByteDelta()
    {
        var tracker = new TransferRateTracker();
        var start = DateTimeOffset.UtcNow;
        tracker.Sample(bytesSoFar: 0, totalBytes: 10_000, start);

        var snapshot = tracker.Sample(bytesSoFar: 1_000, totalBytes: 10_000, start.AddSeconds(1));

        snapshot.BytesPerSecond.Should().Be(1000);
    }

    [Fact]
    public void Sample_ComputesEtaFromRemainingBytesAndRate()
    {
        var tracker = new TransferRateTracker();
        var start = DateTimeOffset.UtcNow;
        tracker.Sample(bytesSoFar: 0, totalBytes: 10_000, start);

        var snapshot = tracker.Sample(bytesSoFar: 1_000, totalBytes: 10_000, start.AddSeconds(1));

        // 9000 bytes remaining at 1000 bytes/sec = 9 seconds
        snapshot.EtaSeconds.Should().Be(9);
    }

    [Fact]
    public void Sample_ZeroTotalBytes_ReturnsNullEta()
    {
        var tracker = new TransferRateTracker();
        var start = DateTimeOffset.UtcNow;
        tracker.Sample(bytesSoFar: 0, totalBytes: 0, start);

        var snapshot = tracker.Sample(bytesSoFar: 0, totalBytes: 0, start.AddSeconds(1));

        snapshot.EtaSeconds.Should().BeNull();
    }

    [Fact]
    public void Sample_SameTimestampTwice_DoesNotThrow()
    {
        var tracker = new TransferRateTracker();
        var start = DateTimeOffset.UtcNow;
        tracker.Sample(bytesSoFar: 0, totalBytes: 10_000, start);

        var act = () => tracker.Sample(bytesSoFar: 500, totalBytes: 10_000, start);

        act.Should().NotThrow();
    }

    [Fact]
    public void Sample_TransferComplete_ReturnsZeroEta()
    {
        var tracker = new TransferRateTracker();
        var start = DateTimeOffset.UtcNow;
        tracker.Sample(bytesSoFar: 0, totalBytes: 1000, start);

        var snapshot = tracker.Sample(bytesSoFar: 1000, totalBytes: 1000, start.AddSeconds(1));

        snapshot.EtaSeconds.Should().Be(0);
    }
}
