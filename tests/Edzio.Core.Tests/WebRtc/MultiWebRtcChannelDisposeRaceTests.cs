using Edzio.Core.Tests.Signaling;
using Edzio.Core.WebRtc;
using FluentAssertions;
using SIPSorcery.Net;
using Xunit;

namespace Edzio.Core.Tests.WebRtc;

/// <summary>
/// Regression test for a receiver-side race: <c>DisposeAsync</c> used to dispose
/// <c>_receiveCts</c>, but a <c>ReceiveAsync</c> call still in flight (or one made
/// after dispose, as happens when the peer disconnects while the channel is
/// tearing down) reads <c>_receiveCts.Token</c> — throwing a raw
/// <see cref="ObjectDisposedException"/> instead of the
/// <see cref="OperationCanceledException"/> callers expect, which
/// <c>ReceiveViewModel</c> surfaced as a hard "Receive failed" error instead of
/// "Transfer cancelled." No lane connection is needed: an unconnected
/// <see cref="WebRtcChannel"/>'s <c>DisposeAsync</c> is a no-op, so this exercises
/// only the <c>_receiveCts</c> lifetime.
/// </summary>
public class MultiWebRtcChannelDisposeRaceTests
{
    [Fact]
    public async Task ReceiveAsync_AfterDispose_DoesNotThrowObjectDisposedException()
    {
        var signaling = new FakeSignalingClient();
        var channel = new MultiWebRtcChannel(
            new RTCConfiguration(), signaling, WebRtcRole.Offerer, laneCount: 1);

        await channel.DisposeAsync();

        var exception = await Record.ExceptionAsync(() => channel.ReceiveAsync());

        exception.Should().NotBeOfType<ObjectDisposedException>(
            "disposal must not leave a receive racing a disposed CancellationTokenSource");
    }
}
