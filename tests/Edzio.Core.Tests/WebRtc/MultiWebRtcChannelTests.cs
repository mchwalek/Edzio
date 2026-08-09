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

        // Drain concurrently with sending — the flush barrier now depends on the
        // peer's merge loop making progress (see FlushAsync's remarks), exactly as
        // TransferSession.ReceiveAsync's own receive loop always runs concurrently
        // with the sender in production. Draining only after every chunk was sent
        // would let the bounded inbound queue back up and block the merge loop
        // before it ever reaches the FlushMarker.
        var seenDone = false;
        var chunksBeforeDone = 0;
        var receiveTask = Task.Run(async () =>
        {
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
        });

        for (var i = 0; i < chunkCount; i++)
        {
            await offerer.SendAsync(new byte[262_135], cts.Token);
        }

        await offerer.FlushAsync(cts.Token);

        // After the barrier, the terminating message cannot overtake anything.
        await offerer.SendAsync([(byte)TransferMessageType.Done], cts.Token);

        await receiveTask;

        seenDone.Should().BeTrue();
        chunksBeforeDone.Should().Be(chunkCount, "Done must arrive after every chunk");
    }

    /// <summary>
    /// Regression test for a send-side lane failure being silently swallowed: before
    /// the fix, a faulted pump only decremented <c>_inFlight</c> in its <c>finally</c>,
    /// so <see cref="MultiWebRtcChannel.FlushAsync"/> would report "drained" even
    /// though a message was dropped by the failed lane. A <c>null</c> payload
    /// deterministically makes the underlying <c>WebRtcChannel</c>'s real
    /// <c>send()</c> throw a <see cref="NullReferenceException"/> (verified via a
    /// throwaway probe against SIPSorcery's <c>RTCDataChannel.send</c>), which
    /// stands in for a realistic WAN send failure without mocking anything.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SendAsync_SurfacesLaneFailure_InsteadOfFlushReportingFalseAllClear()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var offerer = new MultiWebRtcChannel(
            config, paired.Offerer, WebRtcRole.Offerer, laneCount: 2);
        await using var answerer = new MultiWebRtcChannel(
            config, paired.Answerer, WebRtcRole.Answerer, laneCount: 2);

        var answererConnect = answerer.ConnectAsync(cts.Token);
        var offererConnect = offerer.ConnectAsync(cts.Token);
        await Task.WhenAll(offererConnect, answererConnect);
        await Task.WhenAll(offerer.WaitForOpenAsync(cts.Token), answerer.WaitForOpenAsync(cts.Token));

        // Whichever lane's pump picks this up will throw inside lane.SendAsync.
        await offerer.SendAsync(null!, cts.Token);

        Func<Task> sendMoreThenFlush = async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await offerer.SendAsync(new byte[16], cts.Token);
            }

            await offerer.FlushAsync(cts.Token);
        };

        await sendMoreThenFlush.Should().ThrowAsync<Exception>(
            "a lane's send failure must surface to the caller instead of FlushAsync " +
            "reporting a false all-clear");
    }

    [Fact]
    public void FlushProtocolMessages_RoundTripTheirLaneIndex()
    {
        var marker = MultiWebRtcChannel.BuildFlushMarker(5);
        MultiWebRtcChannel.TryReadFlushProtocolMessage(marker, out var markerType, out var markerLane)
            .Should().BeTrue();
        markerType.Should().Be(TransferMessageType.FlushMarker);
        markerLane.Should().Be(5);

        var ack = MultiWebRtcChannel.BuildFlushAck(3);
        MultiWebRtcChannel.TryReadFlushProtocolMessage(ack, out var ackType, out var ackLane)
            .Should().BeTrue();
        ackType.Should().Be(TransferMessageType.FlushAck);
        ackLane.Should().Be(3);

        // A real chunk message must never be misread as a flush-protocol message.
        var chunk = new byte[9 + 16];
        chunk[0] = (byte)TransferMessageType.Chunk;
        MultiWebRtcChannel.TryReadFlushProtocolMessage(chunk, out _, out _).Should().BeFalse();
    }

    /// <summary>
    /// Regression test for the flush-protocol interception in <c>StartMergeAsync</c>:
    /// if the <c>continue</c> after handling a FlushMarker/FlushAck were ever dropped,
    /// those 5-byte control messages would leak into the app-visible receive stream
    /// and be indistinguishable from real (if malformed) chunk data, silently
    /// corrupting the transfer. Sends real chunks, flushes (which now performs a real
    /// FlushMarker/FlushAck round trip on every lane), then sends one more real
    /// message — the receiver must see exactly the messages that were actually sent,
    /// nothing extra and nothing missing.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task FlushAsync_ProtocolMessages_NeverLeakIntoReceiveAsync()
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

        const int chunkCount = 6;
        var sent = new List<int>();

        // Drain concurrently with sending — see the matching comment in
        // FlushAsync_ReturnsOnlyAfterEveryQueuedMessageHasLeft for why draining
        // only after every chunk was sent would deadlock the bounded inbound queue.
        var receivedInts = new List<int>();
        var sawDone = false;
        var receiveTask = Task.Run(async () =>
        {
            for (var i = 0; i < chunkCount + 1; i++)
            {
                var msg = await answerer.ReceiveAsync(cts.Token);
                if (msg.Length == 1 && msg[0] == (byte)TransferMessageType.Done)
                {
                    sawDone.Should().BeFalse("Done must be seen exactly once");
                    sawDone = true;
                }
                else
                {
                    msg.Length.Should().Be(1024, "no flush-protocol message should ever surface here");
                    receivedInts.Add(BitConverter.ToInt32(msg));
                }
            }
        });

        for (var i = 0; i < chunkCount; i++)
        {
            sent.Add(i);
            var payload = new byte[1024];
            BitConverter.TryWriteBytes(payload.AsSpan(), i);
            await offerer.SendAsync(payload, cts.Token);
        }

        await offerer.FlushAsync(cts.Token);
        await offerer.SendAsync([(byte)TransferMessageType.Done], cts.Token);

        await receiveTask;

        sawDone.Should().BeTrue();
        receivedInts.Should().BeEquivalentTo(sent);
    }
}
