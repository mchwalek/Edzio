using System.Threading.Channels;
using Edzio.Core.Transfer;
using Edzio.Core.WebRtc;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.WebRtc;

/// <summary>
/// Deterministic, real-time-independent tests for
/// <see cref="MultiWebRtcChannel.ReceiveOrTimeoutAsync"/> — the idle-timeout
/// mechanism used by <c>ReceiveAsync</c> to break SCTP head-of-line blocking on
/// ordered channels when chunks are lost. Mirrors
/// <see cref="MultiWebRtcChannelDisposeLogicTests"/>: a fake <c>delay</c> function
/// lets these run instantly instead of waiting out a real 30-second timeout. See
/// also <c>MultiWebRtcChannelTests</c> for end-to-end send/receive coverage over
/// real paired channels.
/// </summary>
public class MultiWebRtcChannelReceiveTimeoutTests
{
    /// <summary>
    /// Regression test: with no inbound message ever arriving, the wait must not
    /// hang forever — once the idle timeout elapses, it must surface as a
    /// <see cref="TransferException"/> rather than a hang or a raw cancellation.
    /// </summary>
    [Fact]
    public async Task ReceiveOrTimeoutAsync_ThrowsTransferException_WhenIdleTimeoutExpires()
    {
        var inbound = Channel.CreateUnbounded<byte[]>();

        var act = () => MultiWebRtcChannel.ReceiveOrTimeoutAsync(
            inbound.Reader,
            CancellationToken.None,
            CancellationToken.None,
            TimeSpan.FromSeconds(30),
            delay: _ => Task.CompletedTask); // simulates the idle timeout elapsing instantly

        await act.Should().ThrowAsync<TransferException>()
            .WithMessage("Receive stalled for 30s with no inbound messages*");
    }

    /// <summary>
    /// The normal (non-stalled) case: a message that arrives on its own must be
    /// returned without waiting for the idle timeout at all.
    /// </summary>
    [Fact]
    public async Task ReceiveOrTimeoutAsync_ReturnsMessage_WhenItArrivesBeforeTimeout()
    {
        var inbound = Channel.CreateUnbounded<byte[]>();
        var payload = new byte[] { 1, 2, 3 };
        await inbound.Writer.WriteAsync(payload);

        // An infinite delay proves the receive branch won on its own — if the
        // implementation waited on the timeout instead, this test would hang.
        var result = await MultiWebRtcChannel.ReceiveOrTimeoutAsync(
            inbound.Reader,
            CancellationToken.None,
            CancellationToken.None,
            TimeSpan.FromSeconds(30),
            delay: _ => new TaskCompletionSource().Task);

        result.Should().BeEquivalentTo(payload);
    }

    /// <summary>
    /// Peer disconnection must unblock a pending receive with a plain
    /// <see cref="OperationCanceledException"/> — not the synthetic
    /// <see cref="TransferException"/> reserved for a genuine idle-timeout stall.
    /// </summary>
    [Fact]
    public async Task ReceiveOrTimeoutAsync_ThrowsOperationCanceled_WhenPeerDisconnects()
    {
        var inbound = Channel.CreateUnbounded<byte[]>();
        using var disconnectedCts = new CancellationTokenSource();
        await disconnectedCts.CancelAsync();

        var act = () => MultiWebRtcChannel.ReceiveOrTimeoutAsync(
            inbound.Reader,
            CancellationToken.None,
            disconnectedCts.Token,
            TimeSpan.FromSeconds(30),
            // An infinite delay proves disconnection — not the idle timeout — is
            // what unblocked the receive.
            delay: _ => new TaskCompletionSource().Task);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Caller cancellation must unblock a pending receive with a plain
    /// <see cref="OperationCanceledException"/>, same as peer disconnection.
    /// </summary>
    [Fact]
    public async Task ReceiveOrTimeoutAsync_ThrowsOperationCanceled_WhenCallerCancels()
    {
        var inbound = Channel.CreateUnbounded<byte[]>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => MultiWebRtcChannel.ReceiveOrTimeoutAsync(
            inbound.Reader,
            cts.Token,
            CancellationToken.None,
            TimeSpan.FromSeconds(30),
            delay: _ => new TaskCompletionSource().Task);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
