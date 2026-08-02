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
    /// Diagnostics must never throw into the transfer path, including when the
    /// caller-supplied log sink itself faults on every call.
    /// </summary>
    [Fact]
    public async Task Start_WhenLogSinkAlwaysThrows_DoesNotPropagate()
    {
        var pc = new RTCPeerConnection(new RTCConfiguration());

        var run = async () =>
        {
            using (SctpDiagnostics.Start(
                pc,
                "probe",
                _ => throw new InvalidOperationException("log sink is down"),
                TimeSpan.FromMilliseconds(10)))
            {
                await Task.Delay(100);
            }
        };

        await run.Should().NotThrowAsync();

        pc.Close("test complete");
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
