using Edzio.Core.Tests.Signaling;
using Edzio.Core.WebRtc;
using FluentAssertions;
using SIPSorcery.Net;
using Xunit;

namespace Edzio.Core.Tests.WebRtc;

public class SctpDiagnosticsTests
{
    /// <summary>
    /// Guard test for the diagnostic reflection walk. Failing after a SIPSorcery
    /// package bump means SctpDiagnostics needs updating for the new version.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TrySample_OnConnectedPeerConnection_ReturnsPlausibleWindowValues()
    {
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

        var sender = SctpDiagnostics.TryResolveDataSender(pc);
        sender.Should().NotBeNull(
            "the reflection walk must locate SctpDataSender in the current SIPSorcery version");

        var sample = SctpDiagnostics.TrySample(sender!);
        sample.Should().NotBeNull(
            "all sampled fields must still exist on SctpDataSender in the current SIPSorcery version");

        // RFC 4960 7.2.1 initial cwnd with SIPSorcery's 1300-byte MTU is 4380 bytes.
        // Anything in this range proves the field was really read, not defaulted to 0.
        sample!.Value.CongestionWindow.Should().BeGreaterThan(0);
        sample.Value.RetransmissionTimeout.Should().BeGreaterThan(0);
    }
}
