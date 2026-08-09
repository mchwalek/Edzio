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

    /// <summary>
    /// Optional predicate: ICE-relay messages matching it are dropped instead of
    /// forwarded. Used to simulate a relay/network where LAN endpoint
    /// advertisements don't reach the sender.
    /// </summary>
    public Func<string, bool>? SuppressIce { get; set; }

    public PairedFakeSignaling()
    {
        // Offerer → Answerer
        Offerer.OnOfferSent  += sdp => Answerer.SimulateOfferReceived(sdp);
        Offerer.OnAnswerSent += sdp => Answerer.SimulateAnswerReceived(sdp);
        Offerer.OnIceSent    += c   => { if (SuppressIce?.Invoke(c) != true) Answerer.SimulateIceCandidateReceived(c); };

        // Answerer → Offerer
        Answerer.OnOfferSent  += sdp => Offerer.SimulateOfferReceived(sdp);
        Answerer.OnAnswerSent += sdp => Offerer.SimulateAnswerReceived(sdp);
        Answerer.OnIceSent    += c   => { if (SuppressIce?.Invoke(c) != true) Offerer.SimulateIceCandidateReceived(c); };
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
    /// Regression test for a follow-up bug found after the fix above shipped:
    /// a *fixed* flush timeout (the original fix used 10 seconds) doesn't scale
    /// to larger files. `TransferSession.SendAsync` calls
    /// <see cref="WebRtcChannel.SendAsync"/> in a tight loop for every chunk with
    /// no throttling, so a large file (e.g. ~84 MB / ~320 chunks in production)
    /// gets dumped into the local SCTP send queue almost instantly — far more
    /// than a fixed timeout can drain in time, so the connection was closed with
    /// most of the data still unsent (observed in production as the receiver
    /// getting stuck partway through, e.g. at 10%).
    ///
    /// This test sends many chunk-sized messages back-to-back (mirroring
    /// TransferSession.SendAsync's loop) and disposes immediately afterward,
    /// then asserts every chunk is still delivered intact and in order. It also
    /// indirectly exercises <see cref="WebRtcChannel"/>'s send-buffer
    /// backpressure and the stall-detecting (not fixed-timeout) flush wait in
    /// <c>DisposeAsync</c>.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SendAsync_ManyChunksBackToBack_ThenImmediateDispose_DeliversAllChunks()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration(); // host candidates only — no STUN needed

        var offererChannel = new WebRtcChannel(config, paired.Offerer, WebRtcRole.Offerer);
        await using var answererChannel = new WebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(50));

        var answererConnectTask = answererChannel.ConnectAsync(cts.Token);
        var offererConnectTask  = offererChannel.ConnectAsync(cts.Token);
        await Task.WhenAll(offererConnectTask, answererConnectTask);

        await Task.WhenAll(
            offererChannel.WaitForOpenAsync(cts.Token),
            answererChannel.WaitForOpenAsync(cts.Token));

        // ~20 chunks of ~256 KB each (~5 MB total) — a scaled-down stand-in for
        // a large multi-chunk file transfer like the 84 MB production case.
        const int chunkCount = 20;
        const int chunkSize = 262135; // matches ChunkEngine.ChunkSize
        var rng = new Random(7);
        var chunks = new byte[chunkCount][];
        for (int i = 0; i < chunkCount; i++)
        {
            chunks[i] = new byte[chunkSize];
            rng.NextBytes(chunks[i]);
        }

        // Mirrors TransferSession.SendAsync: send every chunk back-to-back with
        // no explicit wait in between.
        foreach (var chunk in chunks)
        {
            await offererChannel.SendAsync(chunk, cts.Token);
        }

        // Mirrors the production pattern: dispose the sender's channel
        // immediately after the last send, with no explicit wait.
        await offererChannel.DisposeAsync();

        for (int i = 0; i < chunkCount; i++)
        {
            var received = await answererChannel.ReceiveAsync(cts.Token);
            received.Should().Equal(chunks[i]);
        }
    }

    /// <summary>
    /// Diagnostic benchmark (not a correctness test) for the slow-transfer
    /// investigation (docs/debug/slow-webrtc-transfer-throughput). Measures raw
    /// <see cref="WebRtcChannel"/> throughput with no disk I/O, no SQLite, and no
    /// TransferSession overhead in the loop — isolates whatever the SIPSorcery
    /// SCTP data channel itself can sustain on a loopback connection. Logs
    /// elapsed time and MB/s via test output so it can be compared before/after
    /// a SIPSorcery version bump.
    /// </summary>
    [Fact(Timeout = 60000, Skip = "Diagnostic benchmark — intentionally 'fails' to report timing. Remove Skip to run.")]
    public async Task Benchmark_RawChannelThroughput_18MB()
    {
        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration(); // host candidates only — no STUN needed

        var offererChannel = new WebRtcChannel(config, paired.Offerer, WebRtcRole.Offerer);
        await using var answererChannel = new WebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(55));

        var answererConnectTask = answererChannel.ConnectAsync(cts.Token);
        var offererConnectTask  = offererChannel.ConnectAsync(cts.Token);
        await Task.WhenAll(offererConnectTask, answererConnectTask);

        await Task.WhenAll(
            offererChannel.WaitForOpenAsync(cts.Token),
            answererChannel.WaitForOpenAsync(cts.Token));

        const int chunkSize = 262135; // matches ChunkEngine.ChunkSize
        const long targetBytes = 18_500_000; // matches the user's test file size
        int chunkCount = (int)((targetBytes + chunkSize - 1) / chunkSize);

        var rng = new Random(7);
        var chunks = new byte[chunkCount][];
        for (int i = 0; i < chunkCount; i++)
        {
            chunks[i] = new byte[chunkSize];
            rng.NextBytes(chunks[i]);
        }

        var receiveTask = Task.Run(async () =>
        {
            for (int i = 0; i < chunkCount; i++)
                await answererChannel.ReceiveAsync(cts.Token);
        }, cts.Token);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var chunk in chunks)
            await offererChannel.SendAsync(chunk, cts.Token);

        await receiveTask; // wait until the answerer has actually received everything
        sw.Stop();

        await offererChannel.DisposeAsync();

        long totalBytes = (long)chunkCount * chunkSize;
        double mbPerSec = totalBytes / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;

        throw new Xunit.Sdk.XunitException(
            $"BENCHMARK RESULT (not a failure): {chunkCount} chunks, {totalBytes:N0} bytes, " +
            $"{sw.Elapsed.TotalSeconds:F2}s, {mbPerSec:F2} MB/s");
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
