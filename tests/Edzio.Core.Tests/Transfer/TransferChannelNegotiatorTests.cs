using Edzio.Core.Lan;
using Edzio.Core.Tests.WebRtc;
using Edzio.Core.Transfer;
using Edzio.Core.WebRtc;
using FluentAssertions;
using SIPSorcery.Net;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

public class TransferChannelNegotiatorTests
{
    [Fact(Timeout = 30000)]
    public async Task BothSides_OnSameHost_EstablishLanDirectChannel()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        // Sender first: with the synchronous fakes, the receiver's advertisement
        // fires during its own startup, so the sender must already be subscribed.
        // (In production, ordering is guaranteed by the signaling round-trip —
        // see the remarks on TransferChannelNegotiator.)
        var senderTask = TransferChannelNegotiator.ConnectAsSenderAsync(
            config, paired.Offerer, ct: cts.Token);
        await Task.Delay(100, cts.Token);
        var receiverTask = TransferChannelNegotiator.ConnectAsReceiverAsync(
            config, paired.Answerer, ct: cts.Token);

        await using var sender = await senderTask;
        await using var receiver = await receiverTask;

        sender.Should().BeOfType<TcpTransferChannel>();
        receiver.Should().BeOfType<TcpTransferChannel>();

        var payload = new byte[100_000];
        new Random(3).NextBytes(payload);
        await sender.SendAsync(payload, cts.Token);
        (await receiver.ReceiveAsync(cts.Token)).Should().Equal(payload);
    }

    [Fact(Timeout = 60000)]
    public async Task LanUnavailable_BothSidesFallBackToWebRtc()
    {
        var paired = new PairedFakeSignaling();

        // Drop LAN endpoint advertisements in the relay: the sender never learns
        // the receiver's endpoint and must fall back to a WebRTC offer.
        paired.SuppressIce = json => json.Contains(LanDirect.AdvertisementJsonKey, StringComparison.Ordinal);

        var config = new RTCConfiguration(); // host candidates only — loopback ICE
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(50));

        var senderTask = TransferChannelNegotiator.ConnectAsSenderAsync(
            config, paired.Offerer, ct: cts.Token);
        await Task.Delay(100, cts.Token);
        var receiverTask = TransferChannelNegotiator.ConnectAsReceiverAsync(
            config, paired.Answerer, ct: cts.Token);

        await using var sender = await senderTask;
        await using var receiver = await receiverTask;

        sender.Should().BeOfType<WebRtcChannel>();
        receiver.Should().BeOfType<WebRtcChannel>();

        var payload = new byte[10_000];
        new Random(3).NextBytes(payload);
        await sender.SendAsync(payload, cts.Token);
        (await receiver.ReceiveAsync(cts.Token)).Should().Equal(payload);
    }

    [Fact(Timeout = 30000)]
    public async Task SctpBurstPeriodPatch_AppliesOnConnectedPeerConnection()
    {
        // Verifies the reflection walk in WebRtcChannel.TryReduceSctpBurstPeriod
        // still finds SIPSorcery's internals — this test failing after a package
        // bump means the walk needs updating for the new version.
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        await using var offerer = new WebRtcChannel(config, paired.Offerer, WebRtcRole.Offerer);
        await using var answerer = new WebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer);

        var answererConnect = answerer.ConnectAsync(cts.Token);
        var offererConnect = offerer.ConnectAsync(cts.Token);
        await Task.WhenAll(offererConnect, answererConnect);
        await Task.WhenAll(offerer.WaitForOpenAsync(cts.Token), answerer.WaitForOpenAsync(cts.Token));

        var pcField = typeof(WebRtcChannel).GetField("_pc",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pc = (RTCPeerConnection)pcField.GetValue(offerer)!;

        WebRtcChannel.TryReduceSctpBurstPeriod(pc).Should().BeTrue(
            "the reflection walk must locate SctpDataSender._burstPeriodMilliseconds in the current SIPSorcery version");
    }
}
