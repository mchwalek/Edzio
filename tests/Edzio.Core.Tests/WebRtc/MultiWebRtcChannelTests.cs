using Edzio.Core.Tests.Signaling;
using Edzio.Core.Transfer;
using Edzio.Core.WebRtc;
using FluentAssertions;
using SIPSorcery.Net;
using Xunit;

namespace Edzio.Core.Tests.WebRtc;

public class MultiWebRtcChannelTests
{
    [Fact(Timeout = 90000)]
    public async Task TwoMultiChannels_DeliverEveryMessage_RegardlessOfOrder()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(80));

        // Two lanes keeps loopback ICE cheap while still exercising the striping,
        // merging and flush paths that a single lane would bypass.
        await using var offerer = new MultiWebRtcChannel(
            config, paired.Offerer, WebRtcRole.Offerer, laneCount: 2);
        await using var answerer = new MultiWebRtcChannel(
            config, paired.Answerer, WebRtcRole.Answerer, laneCount: 2);

        var answererConnect = answerer.ConnectAsync(cts.Token);
        var offererConnect = offerer.ConnectAsync(cts.Token);
        await Task.WhenAll(offererConnect, answererConnect);
        await Task.WhenAll(offerer.WaitForOpenAsync(cts.Token), answerer.WaitForOpenAsync(cts.Token));

        const int messageCount = 20;
        var expected = new List<int>();
        for (var i = 0; i < messageCount; i++)
        {
            expected.Add(i);
            var payload = new byte[1024];
            BitConverter.TryWriteBytes(payload.AsSpan(), i);
            await offerer.SendAsync(payload, cts.Token);
        }

        var received = new List<int>();
        for (var i = 0; i < messageCount; i++)
        {
            var msg = await answerer.ReceiveAsync(cts.Token);
            received.Add(BitConverter.ToInt32(msg));
        }

        // Striping across independent associations does not preserve order.
        received.Should().BeEquivalentTo(expected);
    }

    [Fact(Timeout = 90000)]
    public async Task FlushAsync_ReturnsOnlyAfterEveryQueuedMessageHasLeft()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(80));

        await using var offerer = new MultiWebRtcChannel(
            config, paired.Offerer, WebRtcRole.Offerer, laneCount: 2);
        await using var answerer = new MultiWebRtcChannel(
            config, paired.Answerer, WebRtcRole.Answerer, laneCount: 2);

        var answererConnect = answerer.ConnectAsync(cts.Token);
        var offererConnect = offerer.ConnectAsync(cts.Token);
        await Task.WhenAll(offererConnect, answererConnect);
        await Task.WhenAll(offerer.WaitForOpenAsync(cts.Token), answerer.WaitForOpenAsync(cts.Token));

        const int chunkCount = 10;
        for (var i = 0; i < chunkCount; i++)
        {
            await offerer.SendAsync(new byte[262_135], cts.Token);
        }

        await offerer.FlushAsync(cts.Token);

        // After the barrier, the terminating message cannot overtake anything.
        await offerer.SendAsync([(byte)TransferMessageType.Done], cts.Token);

        var seenDone = false;
        var chunksBeforeDone = 0;
        for (var i = 0; i < chunkCount + 1; i++)
        {
            var msg = await answerer.ReceiveAsync(cts.Token);
            if (msg.Length == 1 && msg[0] == (byte)TransferMessageType.Done)
            {
                seenDone = true;
                break;
            }

            chunksBeforeDone++;
        }

        seenDone.Should().BeTrue();
        chunksBeforeDone.Should().Be(chunkCount, "Done must arrive after every chunk");
    }

    [Fact]
    public void DefaultLaneCount_IsEight()
    {
        // Derived from ~34 KB in flight needed for 6.75 MB/s at 5 ms RTT, over the
        // ~4380-byte per-association congestion window. See the design spec.
        MultiWebRtcChannel.DefaultLaneCount.Should().Be(8);
    }
}
