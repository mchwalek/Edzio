using Edzio.Core.WebRtc;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using Xunit;

namespace Edzio.Core.Tests.WebRtc;

public class WebRtcChannelDiagnosticsTests
{
    /// <summary>
    /// Guard test for the diagnostics wiring in <see cref="WebRtcChannel"/>: an open
    /// channel must report which ICE pair carries the traffic. This line is what the
    /// WAN measurement reads, so losing it — through a wiring regression or a
    /// SIPSorcery package bump moving <c>RtpIceChannel.NominatedEntry</c> — must fail
    /// here rather than show up as a silently empty log during the manual
    /// two-machine run.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task OpenChannel_LogsNominatedIcePair()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration(); // host candidates only — loopback ICE
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var log = new CapturingLogger();

        await using var offerer = new WebRtcChannel(config, paired.Offerer, WebRtcRole.Offerer, log);
        await using var answerer = new WebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer);

        var answererConnect = answerer.ConnectAsync(cts.Token);
        var offererConnect = offerer.ConnectAsync(cts.Token);
        await Task.WhenAll(offererConnect, answererConnect);
        await Task.WhenAll(offerer.WaitForOpenAsync(cts.Token), answerer.WaitForOpenAsync(cts.Token));

        await WaitUntilAsync(() => log.Any("ICE nominated pair:"));

        log.Lines.Should().Contain(l => l.Contains("ICE nominated pair:"),
            "the walk to RtpIceChannel.NominatedEntry must still resolve in the current SIPSorcery version");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }
    }

    private sealed class CapturingLogger : ILogger<WebRtcChannel>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) { return _lines.ToList(); } }
        }

        public bool Any(string fragment)
        {
            lock (_lines) { return _lines.Any(l => l.Contains(fragment, StringComparison.Ordinal)); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) { _lines.Add(formatter(state, exception)); }
        }
    }
}
