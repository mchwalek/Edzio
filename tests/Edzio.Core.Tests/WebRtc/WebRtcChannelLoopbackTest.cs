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
    /// Unit-level smoke test: ConnectAsync wires up the peer connection and
    /// sends the offer through signaling without throwing.
    /// Does not attempt ICE negotiation.
    /// </summary>
    [Fact]
    public async Task Offerer_ConnectAsync_SendsOffer()
    {
        var fake = new FakeSignalingClient();
        var config = new RTCConfiguration();

        await using var channel = new WebRtcChannel(config, fake, WebRtcRole.Offerer);
        await channel.ConnectAsync();

        fake.SentOffers.Should().HaveCount(1);
        fake.SentOffers[0].Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Unit-level smoke test: Answerer subscribes to the offer event without
    /// throwing during ConnectAsync.
    /// </summary>
    [Fact]
    public async Task Answerer_ConnectAsync_SubscribesToOffer()
    {
        var fake = new FakeSignalingClient();
        var config = new RTCConfiguration();

        await using var channel = new WebRtcChannel(config, fake, WebRtcRole.Answerer);

        // Should complete immediately (no awaits in answerer path before the event fires)
        var act = async () => await channel.ConnectAsync();
        await act.Should().NotThrowAsync();
    }
}
