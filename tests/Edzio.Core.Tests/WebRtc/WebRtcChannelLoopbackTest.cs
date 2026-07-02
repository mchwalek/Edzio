using Edzio.Core.Tests.Signaling;
using Edzio.Core.WebRtc;
using FluentAssertions;
using SIPSorcery.Net;
using Xunit;

namespace Edzio.Core.Tests.WebRtc;

/// <summary>
/// Wires two FakeSignalingClients together so messages from one reach the
/// other, simulating the server relay without network I/O.
/// </summary>
public class PairedFakeSignaling
{
    public FakeSignalingClient Offerer { get; } = new();
    public FakeSignalingClient Answerer { get; } = new();

    public PairedFakeSignaling()
    {
        // Offerer → Answerer
        Offerer.OnOfferSent  += sdp => Answerer.SimulateOfferReceived(sdp);
        Offerer.OnAnswerSent += sdp => Answerer.SimulateAnswerReceived(sdp);
        Offerer.OnIceSent    += c   => Answerer.SimulateIceCandidateReceived(c);

        // Answerer → Offerer
        Answerer.OnOfferSent  += sdp => Offerer.SimulateOfferReceived(sdp);
        Answerer.OnAnswerSent += sdp => Offerer.SimulateAnswerReceived(sdp);
        Answerer.OnIceSent    += c   => Offerer.SimulateIceCandidateReceived(c);
    }
}

public class WebRtcChannelLoopbackTest
{
    /// <summary>
    /// Regression test for the answerer-side WaitForOpenAsync hang.
    ///
    /// SIPSorcery fires <c>ondatachannel</c> only after the SCTP open procedure
    /// completes, meaning the <see cref="RTCDataChannel"/> is already in the
    /// <c>open</c> state when our callback runs. The previous code subscribed
    /// <c>dc.onopen</c> inside that callback — which would never fire — so
    /// <c>_channelOpen</c> was never resolved and <c>WaitForOpenAsync</c> hung
    /// forever on the answerer side.
    ///
    /// This test requires real host ICE candidates (loopback). Run manually or
    /// in environments with loopback network interfaces.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task WaitForOpenAsync_Answerer_CompletesAfterDataChannelIsReceived()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration(); // host candidates only — no STUN needed

        await using var offererChannel  = new WebRtcChannel(config, paired.Offerer,  WebRtcRole.Offerer);
        await using var answererChannel = new WebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Start the answerer first. Its ConnectAsync runs synchronously through all
        // its subscriptions (OfferReceived, ondatachannel) before hitting its first
        // await at line 204 and returning the incomplete task. The offerer task is
        // started after, so when SendOfferAsync fires, the answerer is already
        // subscribed and will receive it.
        // (createDataChannel is synchronous pre-connection, so the offerer would
        // otherwise send the offer before Task.WhenAll ever starts the answerer.)
        var answererConnectTask = answererChannel.ConnectAsync(cts.Token);
        var offererConnectTask  = offererChannel.ConnectAsync(cts.Token);
        await Task.WhenAll(offererConnectTask, answererConnectTask);

        // Both sides must resolve WaitForOpenAsync within a reasonable window.
        // Before the fix, the answerer's _channelOpen TCS was never set because
        // dc.onopen had already fired before WireDataChannel subscribed to it,
        // so this assertion would time out on the answerer side.
        Func<Task> open = () => Task.WhenAll(
            offererChannel.WaitForOpenAsync(cts.Token),
            answererChannel.WaitForOpenAsync(cts.Token));

        await open.Should().CompleteWithinAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// End-to-end loopback: two WebRtcChannels in the same process exchange
    /// data over a real SIPSorcery RTCPeerConnection (host ICE candidates,
    /// no STUN required).
    /// </summary>
    [Fact(Timeout = 30000, Skip = "Integration - requires loopback ICE negotiation; run manually")]
    public async Task TwoChannels_ExchangeData_Bidirectionally()
    {
        var paired = new PairedFakeSignaling();

        // No STUN — loopback ICE via host candidates (127.0.0.1)
        var config = new RTCConfiguration();

        await using var offererChannel  = new WebRtcChannel(config, paired.Offerer,  WebRtcRole.Offerer);
        await using var answererChannel = new WebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer);

        await Task.WhenAll(
            offererChannel.ConnectAsync(),
            answererChannel.ConnectAsync());

        await Task.WhenAll(
            offererChannel.WaitForOpenAsync(),
            answererChannel.WaitForOpenAsync());

        var message = new byte[] { 1, 2, 3, 4, 5 };
        await offererChannel.SendAsync(message);
        var received = await answererChannel.ReceiveAsync();
        received.Should().Equal(message);
    }

    /// <summary>
    /// Regression test for the "sender claims complete but receiver never gets
    /// the data" bug: SCTP <c>send()</c> only enqueues data for later
    /// asynchronous transmission. Disposing the channel (which calls
    /// <c>_pc.close()</c>) immediately after the last <c>send()</c> call, with
    /// no wait for the outbound SCTP send buffer to drain, aborts the
    /// association before a large (multi-packet) payload has actually been
    /// transmitted — so the receiver never gets it, even though the sender's
    /// local <c>send()</c> call "succeeded".
    ///
    /// This test sends a large (~256 KB, several-packet) payload and disposes
    /// the sender's channel immediately afterward (mirroring the production
    /// `await using` pattern in SendViewModel/TransferSession), then asserts
    /// the receiver still gets the full, correct payload.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SendAsync_LargePayload_ThenImmediateDispose_StillDeliversToReceiver()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration(); // host candidates only — no STUN needed

        var offererChannel = new WebRtcChannel(config, paired.Offerer, WebRtcRole.Offerer);
        await using var answererChannel = new WebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var answererConnectTask = answererChannel.ConnectAsync(cts.Token);
        var offererConnectTask  = offererChannel.ConnectAsync(cts.Token);
        await Task.WhenAll(offererConnectTask, answererConnectTask);

        await Task.WhenAll(
            offererChannel.WaitForOpenAsync(cts.Token),
            answererChannel.WaitForOpenAsync(cts.Token));

        // A large, deterministic payload — big enough to require SCTP
        // fragmentation across many UDP packets (like a real ChunkEngine chunk).
        var message = new byte[262135];
        new Random(42).NextBytes(message);

        await offererChannel.SendAsync(message, cts.Token);

        // Mirrors the production pattern: dispose the sender's channel
        // immediately after the last send, with no explicit wait.
        await offererChannel.DisposeAsync();

        var received = await answererChannel.ReceiveAsync(cts.Token);
        received.Should().Equal(message);
    }

    /// <summary>
    /// Integration test: full SDP exchange + ICE negotiation between two
    /// in-process channels. Requires real network interfaces (host ICE candidates).
    /// Run manually — not in CI.
    /// </summary>
    [Fact(Timeout = 30000, Skip = "Integration - requires real network interfaces; run manually")]
    public async Task ConnectAsync_PairedChannels_ExchangeSdp()
    {
        var paired = new PairedFakeSignaling();
        var config  = new RTCConfiguration();

        await using var offererChannel  = new WebRtcChannel(config, paired.Offerer,  WebRtcRole.Offerer);
        await using var answererChannel = new WebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer);

        await Task.WhenAll(
            offererChannel.ConnectAsync(),
            answererChannel.ConnectAsync());

        paired.Offerer.SentOffers.Should().HaveCount(1);
        paired.Offerer.SentOffers[0].Should().NotBeNullOrWhiteSpace();
        paired.Answerer.SentAnswers.Should().HaveCount(1);
        paired.Answerer.SentAnswers[0].Should().NotBeNullOrWhiteSpace();
    }
}
