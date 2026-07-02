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
