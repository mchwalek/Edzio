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

        // TrySample returns null if any single member is missing, so a non-null sample
        // already proves the whole walk resolved. These two only add that the values
        // were really read rather than left at a zero default.
        sample!.Value.CongestionWindow.Should().BeGreaterThan(0);
        sample.Value.RetransmissionTimeout.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// A sampling loop that dies must say so before it goes. Silence is otherwise
    /// indistinguishable from a healthy link that simply had nothing to report,
    /// which would make the WAN measurement unreadable.
    /// </summary>
    [Fact]
    public async Task Start_WhenSamplingLoopFaults_LogsThatItStopped()
    {
        var pc = new RTCPeerConnection(new RTCConfiguration());
        var lines = new List<string>();

        void Sink(string line)
        {
            lock (lines) { lines.Add(line); }
        }

        // A negative interval faults Task.Delay on the first pass. It is the cheapest
        // way to kill the loop from outside without faking SIPSorcery's internals — a
        // throwing log sink no longer works, because SafeLog absorbs it by design.
        using (SctpDiagnostics.Start(pc, "probe", Sink, TimeSpan.FromMilliseconds(-5)))
        {
            await WaitUntilAsync(() =>
            {
                lock (lines) { return lines.Any(l => l.Contains("stopped")); }
            });
        }

        pc.Close("test complete");

        lock (lines)
        {
            lines.Should().ContainSingle(l => l.Contains("stopped"),
                "the loop must report its own death exactly once, not on every iteration")
                .Which.Should().Contain(nameof(ArgumentOutOfRangeException));
        }
    }

    /// <summary>
    /// A faulting log sink must not kill the sampler. Absorbing the fault is what
    /// stops a transient sink problem from silently ending a WAN measurement that
    /// may already be minutes long.
    /// </summary>
    [Fact]
    public async Task Start_WhenLogSinkThrowsThenRecovers_KeepsSampling()
    {
        var pc = new RTCPeerConnection(new RTCConfiguration());
        var lines = new List<string>();
        var faultsRemaining = 3;

        // Faults the first three writes, then records normally. Without the loop's
        // fault absorption the very first throw ends sampling and nothing is ever
        // recorded, so an empty list is the falsifying observation.
        void Sink(string line)
        {
            lock (lines)
            {
                if (faultsRemaining > 0)
                {
                    faultsRemaining--;
                    throw new InvalidOperationException("log sink is down");
                }

                lines.Add(line);
            }
        }

        using (SctpDiagnostics.Start(pc, "probe", Sink, TimeSpan.FromMilliseconds(10)))
        {
            await WaitUntilAsync(() => { lock (lines) { return lines.Count > 0; } });
        }

        pc.Close("test complete");

        lock (lines)
        {
            faultsRemaining.Should().Be(0,
                "the sampler must have kept calling the sink through every fault");
            lines.Should().NotBeEmpty("sampling must survive a faulting log sink")
                .And.OnlyContain(l => l.Contains("cwnd="));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }
    }
}
