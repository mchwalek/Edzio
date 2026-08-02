using Edzio.Core.Signaling;
using Edzio.Core.Transfer;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using System.Text.Json;
using System.Threading.Channels;

namespace Edzio.Core.WebRtc;

/// <summary>
/// WebRTC data-channel transport. Call <see cref="ConnectAsync"/> once to
/// start signalling, then use <see cref="WaitForOpenAsync"/> before sending.
/// </summary>
public sealed class WebRtcChannel : ITransferChannel
{
    /// <summary>
    /// Backpressure threshold for <see cref="SendAsync"/>: once the SCTP
    /// association's outbound send queue (<see cref="RTCDataChannel.bufferedAmount"/>)
    /// exceeds this many bytes, further sends wait for it to drain before enqueuing
    /// more data. Without this, a large file gets dumped into the local send queue
    /// almost instantly regardless of real network throughput, which both (a) makes
    /// per-chunk progress reporting meaningless (it reflects "enqueued", not "sent"),
    /// and (b) leaves an unbounded amount of data still queued when the caller is
    /// done sending and disposes the channel.
    /// </summary>
    private const ulong MaxBufferedAmount = 1024 * 1024; // 1 MiB

    /// <summary>
    /// Poll interval while waiting for send-buffer backpressure or drain to resolve.
    /// </summary>
    private static readonly TimeSpan BufferPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// How long <see cref="FlushOutboundDataAsync"/> will keep waiting after the last
    /// observed decrease in <see cref="RTCDataChannel.bufferedAmount"/> before giving
    /// up and closing anyway. As long as the buffer keeps shrinking, the wait
    /// continues indefinitely — this only fires when transmission has genuinely
    /// stalled (e.g. the peer vanished), not merely because a large file takes a
    /// while to send.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Extra grace period after <see cref="RTCDataChannel.bufferedAmount"/> reaches
    /// zero, to allow the final fragment(s) a moment to actually leave the socket.
    /// </summary>
    private static readonly TimeSpan FlushGracePeriod = TimeSpan.FromMilliseconds(250);

    private readonly RTCConfiguration _rtcConfig;
    private readonly ISignalingClient _signaling;
    private readonly WebRtcRole _role;
    private readonly ILogger<WebRtcChannel>? _logger;

    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dataChannel;

    private readonly Channel<byte[]> _incoming =
        Channel.CreateBounded<byte[]>(64);

    private readonly TaskCompletionSource _channelOpen =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WebRtcChannel(
        RTCConfiguration rtcConfig,
        ISignalingClient signaling,
        WebRtcRole role,
        ILogger<WebRtcChannel>? logger = null)
    {
        _rtcConfig = rtcConfig;
        _signaling = signaling;
        _role = role;
        _logger = logger;
    }

    private void Log(string msg) => _logger?.LogInformation("{Msg}", msg);

    /// <summary>
    /// Performs the full signalling exchange (offer/answer + ICE) and returns once
    /// both sides have set their remote descriptions. The data channel may still be
    /// opening; call <see cref="WaitForOpenAsync"/> before sending data.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Log($"[{_role}] ConnectAsync started");

        // ── Subscribe to signaling events BEFORE constructing RTCPeerConnection ──
        // RTCPeerConnection() can block for hundreds of milliseconds on the first
        // call (ICE agent setup, DTLS certificate generation). On a fast LAN the
        // remote peer can deliver the offer, answer, or ICE candidates during that
        // window. Subscribing here — before the ctor — ensures no messages are lost
        // while the peer connection is initialising.
        // (Root cause of production hang in session 20:38: offer arrived 402 ms into
        // ConnectAsync; OfferReceived subscription wasn't in place until 646 ms in.)

        // ICE candidate buffer — initialised here so the IceCandidateReceived
        // handler below can reference it safely before _pc is constructed.
        // _pc?.addIceCandidate uses a null-conditional, so buffering works even
        // when _pc is not yet assigned.
        var pendingCandidates = new List<RTCIceCandidateInit>();
        var remoteDescSet = false;
        var candidateLock = new object();
        var localCandidateCount = 0;
        var remoteCandidateCount = 0;

        // Role-specific TCS: whichever side we are, subscribe immediately so the
        // first SignalR message is never dropped.
        TaskCompletionSource<string> answerTcs = null!;
        TaskCompletionSource<string> offerTcs  = null!;

        if (_role == WebRtcRole.Offerer)
        {
            answerTcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _signaling.AnswerReceived += (_, sdp) =>
            {
                Log($"[{_role}] Answer received from signaling");
                answerTcs.TrySetResult(sdp);
            };
        }
        else
        {
            offerTcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _signaling.OfferReceived += (_, sdp) =>
            {
                Log($"[{_role}] Offer received from signaling");
                offerTcs.TrySetResult(sdp);
            };
        }

        // ── Buffer remote ICE candidates until remote description is set ─────
        _signaling.IceCandidateReceived += (_, json) =>
        {
            // LAN endpoint advertisements are piggybacked on the ICE relay by
            // TransferChannelNegotiator — they are not ICE candidates.
            if (json.Contains(Lan.LanDirect.AdvertisementJsonKey, StringComparison.Ordinal))
                return;

            try
            {
                var doc = JsonDocument.Parse(json);
                var init = new RTCIceCandidateInit
                {
                    candidate = doc.RootElement
                        .GetProperty("candidate").GetString() ?? string.Empty,
                    sdpMid = doc.RootElement
                        .TryGetProperty("sdpMid", out var mid)
                            ? mid.GetString() ?? string.Empty
                            : string.Empty,
                    sdpMLineIndex = doc.RootElement
                        .TryGetProperty("sdpMLineIndex", out var idx)
                            ? idx.GetUInt16()
                            : (ushort)0
                };
                remoteCandidateCount++;
                lock (candidateLock)
                {
                    if (remoteDescSet)
                    {
                        Log($"[{_role}] Applied remote ICE candidate #{remoteCandidateCount}: {init.candidate}");
                        _pc?.addIceCandidate(init);
                    }
                    else
                    {
                        Log($"[{_role}] Buffered remote ICE candidate #{remoteCandidateCount} (remote desc not yet set)");
                        pendingCandidates.Add(init);
                    }
                }
            }
            catch (Exception ex) { Log($"[{_role}] Malformed ICE candidate JSON: {ex.Message}"); }
        };

        // ── Now construct the peer connection ────────────────────────────────
        _pc = new RTCPeerConnection(_rtcConfig);

        // Log ICE + connection state transitions — critical for diagnosing hangs.
        // Which ICE path won matters for throughput analysis: a relayed (TURN) pair
        // has very different characteristics from a direct host/srflx pair, and
        // mistaking one for the other would invalidate any congestion measurement.
        _pc.oniceconnectionstatechange += state =>
        {
            Log($"[{_role}] ICE connection state → {state}");
            if (state != RTCIceConnectionState.connected) return;

            var pair = _pc is null ? null : TryGetNominatedIcePair(_pc);
            Log(pair is null
                ? $"[{_role}] ICE connected; nominated pair unavailable"
                : $"[{_role}] ICE nominated pair: local={pair.LocalCandidate?.type} remote={pair.RemoteCandidate?.type}");
        };
        _pc.onconnectionstatechange += state =>
        {
            Log($"[{_role}] Peer connection state → {state}");

            // If the connection fails outright (as opposed to a normal close
            // triggered by our own DisposeAsync after a successful transfer),
            // unblock any pending WaitForOpenAsync/ReceiveAsync/SendAsync calls
            // with a clear error instead of leaving them hanging forever.
            if (state == RTCPeerConnectionState.failed)
            {
                var ex = new TransferException(
                    $"WebRTC connection failed (peer connection state: {state}).");
                _channelOpen.TrySetException(ex);
                _incoming.Writer.TryComplete(ex);
            }
        };
        _pc.onsignalingstatechange += () =>
            Log($"[{_role}] Signaling state → {_pc?.signalingState}");
        _pc.onicegatheringstatechange += state =>
            Log($"[{_role}] ICE gathering state → {state}");

        // ── Forward our ICE candidates to the remote peer ───────────────────
        _pc.onicecandidate += candidate =>
        {
            localCandidateCount++;
            Log($"[{_role}] Local ICE candidate #{localCandidateCount}: {candidate.candidate}");
            var json = JsonSerializer.Serialize(new
            {
                candidate = candidate.candidate,
                sdpMid = candidate.sdpMid,
                sdpMLineIndex = candidate.sdpMLineIndex
            });
            _ = _signaling.SendIceCandidateAsync(json);
        };

        // ── Apply remote description and flush buffered candidates ───────────
        void ApplyRemoteDescription(RTCSessionDescriptionInit desc)
        {
            Log($"[{_role}] Setting remote description (type={desc.type})");
            _pc?.setRemoteDescription(desc);
            lock (candidateLock)
            {
                remoteDescSet = true;
                Log($"[{_role}] Flushing {pendingCandidates.Count} buffered remote candidate(s)");
                foreach (var c in pendingCandidates)
                    _pc?.addIceCandidate(c);
                pendingCandidates.Clear();
            }
        }

        // ── Helper to wire a data channel once we have it ───────────────────
        void WireDataChannel(RTCDataChannel dc)
        {
            _dataChannel = dc;
            dc.onopen += () =>
            {
                Log($"[{_role}] Data channel OPEN");
                ApplySctpPacingWorkaround();
                _channelOpen.TrySetResult();
            };
            dc.onclose += () => Log($"[{_role}] Data channel closed");
            dc.onmessage += (_, _, data) => _incoming.Writer.TryWrite(data);

            // On the answerer side, SIPSorcery fires ondatachannel only after the
            // SCTP OPEN/ACK exchange completes — so the channel is already open by
            // the time this callback runs. dc.onopen has already fired (or will never
            // fire) before we subscribed, so we must resolve _channelOpen immediately
            // when we detect the channel is already in the open state.
            if (dc.readyState == RTCDataChannelState.open)
            {
                Log($"[{_role}] Data channel already OPEN on receipt — resolving immediately");
                ApplySctpPacingWorkaround();
                _channelOpen.TrySetResult();
            }
        }

        if (_role == WebRtcRole.Offerer)
        {
            Log($"[{_role}] Creating data channel...");
            var dc = await _pc.createDataChannel("edzio");
            WireDataChannel(dc);
            Log($"[{_role}] Data channel created");

            Log($"[{_role}] Creating offer...");
            var offer = _pc.createOffer();
            Log($"[{_role}] Setting local description (offer)...");
            await _pc.setLocalDescription(offer);
            Log($"[{_role}] Local description set, sending offer via signaling...");
            await _signaling.SendOfferAsync(offer.sdp);
            Log($"[{_role}] Offer sent, waiting for answer...");

            var answerSdp = await answerTcs.Task.WaitAsync(ct);
            ApplyRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answerSdp
            });
            Log($"[{_role}] SDP exchange complete, waiting for data channel to open...");
        }
        else
        {
            _pc.ondatachannel += dc =>
            {
                Log($"[{_role}] Data channel received from offerer");
                WireDataChannel(dc);
            };

            Log($"[{_role}] Waiting for offer from offerer...");
            var offerSdp = await offerTcs.Task.WaitAsync(ct);
            ApplyRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offerSdp
            });

            Log($"[{_role}] Creating answer...");
            var answer = _pc.createAnswer();
            Log($"[{_role}] Setting local description (answer)...");
            await _pc.setLocalDescription(answer);
            Log($"[{_role}] Local description set, sending answer via signaling...");
            await _signaling.SendAnswerAsync(answer.sdp);
            Log($"[{_role}] Answer sent, SDP exchange complete, waiting for data channel to open...");
        }
    }

    /// <summary>
    /// Works around SIPSorcery's SCTP sender pacing (upstream issues #1088/#1391):
    /// <c>SctpDataSender.DoSend</c> transmits at most <c>MAX_BURST</c> (4) packets
    /// per wake and then sleeps up to <c>_burstPeriodMilliseconds</c> (50 ms)
    /// unless a SACK arrives, capping throughput at roughly
    /// <c>4 × MTU / RTT</c> — ~1 MB/s on a typical Wi-Fi LAN. Shrinking the
    /// internal burst period to 1 ms via reflection lets the sender wake often
    /// enough to keep the (still cwnd/arwnd-gated) pipe full. MAX_BURST itself
    /// is deliberately left alone: it is a const, and upstream reports show
    /// raising it destabilizes the association.
    /// </summary>
    private void ApplySctpPacingWorkaround()
    {
        var applied = _pc is not null && TryReduceSctpBurstPeriod(_pc);
        Log($"[{_role}] SCTP pacing workaround {(applied ? "applied" : "NOT applied — SIPSorcery internals changed?")}");
    }

    /// <summary>
    /// Reflection walk: RTCPeerConnection → sctp transport → SCTP association →
    /// SctpDataSender → internal <c>_burstPeriodMilliseconds</c> field.
    /// Member lookups are by type name rather than member name where possible so
    /// minor upstream refactors don't silently break the walk. Returns false
    /// (never throws) if anything is missing.
    /// </summary>
    internal static bool TryReduceSctpBurstPeriod(RTCPeerConnection pc)
    {
        try
        {
            object? transport = pc.sctp;
            if (transport is null) return false;

            var association = FindMemberValueByTypeName(transport, "SctpAssociation");
            if (association is null) return false;

            var sender = FindMemberValueByTypeName(association, "SctpDataSender");
            if (sender is null) return false;

            var field = sender.GetType().GetField("_burstPeriodMilliseconds",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field is null) return false;

            field.SetValue(sender, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reflection walk: RTCPeerConnection → its private <c>RtpIceChannel</c> →
    /// the nominated checklist entry, i.e. the candidate pair actually carrying
    /// traffic. SIPSorcery 8.0.23 exposes no public accessor for the ICE channel,
    /// so only that first hop is reflective; everything past it is typed.
    /// Returns null (never throws) if the internals moved or nothing is nominated.
    /// </summary>
    internal static ChecklistEntry? TryGetNominatedIcePair(RTCPeerConnection pc)
    {
        try
        {
            var iceChannel = FindMemberValueByTypeName(pc, "RtpIceChannel") as RtpIceChannel;
            return iceChannel?.NominatedEntry;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the first field or property on <paramref name="instance"/> whose declared
    /// type name contains <paramref name="typeNameFragment"/>, and returns its value.
    /// Matching by type name rather than member name survives upstream renames.
    /// </summary>
    internal static object? FindMemberValueByTypeName(object instance, string typeNameFragment)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var f in type.GetFields(flags))
            {
                if (MatchesTypeName(f.FieldType, typeNameFragment) && f.GetValue(instance) is { } value)
                    return value;
            }
            foreach (var p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length == 0
                    && MatchesTypeName(p.PropertyType, typeNameFragment)
                    && p.GetValue(instance) is { } value)
                    return value;
            }
        }
        return null;

        static bool MatchesTypeName(Type t, string fragment)
        {
            for (Type? cur = t; cur is not null; cur = cur.BaseType)
            {
                if (cur.Name.Contains(fragment, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        await WaitForOpenAsync(ct);
        await WaitForSendBufferSpaceAsync(ct);
        _dataChannel!.send(data);
    }

    /// <summary>
    /// Applies backpressure so <see cref="SendAsync"/> doesn't dump an entire large
    /// file into the local SCTP send queue faster than it can actually be
    /// transmitted. See <see cref="MaxBufferedAmount"/>.
    /// </summary>
    private async Task WaitForSendBufferSpaceAsync(CancellationToken ct)
    {
        while (_dataChannel is not null && _dataChannel.bufferedAmount > MaxBufferedAmount)
        {
            await Task.Delay(BufferPollInterval, ct);
        }
    }

    /// <inheritdoc/>
    public Task<byte[]> ReceiveAsync(CancellationToken ct = default)
        => _incoming.Reader.ReadAsync(ct).AsTask();

    /// <inheritdoc/>
    public Task WaitForOpenAsync(CancellationToken ct = default)
        => _channelOpen.Task.WaitAsync(ct);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await FlushOutboundDataAsync();

        _pc?.close();
        _pc?.Dispose();
        _incoming.Writer.TryComplete();
    }

    /// <summary>
    /// Waits for the SCTP association's outbound send queue to drain before the
    /// caller closes the connection.
    /// </summary>
    /// <remarks>
    /// <see cref="RTCDataChannel.send(byte[])"/> only enqueues data on the local
    /// SCTP association for later asynchronous transmission — it does not block
    /// until the data is actually on the wire. Closing the peer connection
    /// immediately after the last <c>send()</c> call (e.g. via <c>await using</c>
    /// right after <see cref="Transfer.TransferSession.SendAsync"/> returns)
    /// aborts the association before large, multi-packet payloads (like a
    /// 256 KB chunk) have any realistic chance of being transmitted, silently
    /// dropping them and leaving the receiver waiting forever. Waiting for
    /// <see cref="RTCDataChannel.bufferedAmount"/> to reach zero — the standard
    /// WebRTC-recommended check before closing a data channel — ensures the send
    /// queue has actually been handed off to the transport first.
    ///
    /// This uses stall-detection rather than a fixed timeout: as long as
    /// <c>bufferedAmount</c> keeps decreasing, waiting continues indefinitely
    /// (so this correctly scales to files of any size), and only gives up after
    /// <see cref="StallTimeout"/> of no progress (i.e. the connection is genuinely
    /// dead, not just slow).
    /// </remarks>
    private async Task FlushOutboundDataAsync()
    {
        if (_dataChannel is null || _dataChannel.readyState != RTCDataChannelState.open)
            return;

        var drained = await WaitForDrainOrStallAsync(
            () => _dataChannel.bufferedAmount,
            BufferPollInterval,
            StallTimeout,
            delay: Task.Delay,
            utcNow: () => DateTimeOffset.UtcNow);

        if (!drained)
        {
            Log($"[{_role}] Outbound send buffer stalled with no progress for " +
                $"{StallTimeout.TotalSeconds}s — closing anyway.");
        }
        else
        {
            // Give the last fragment(s) a brief moment to actually leave the socket.
            await Task.Delay(FlushGracePeriod);
        }
    }

    /// <summary>
    /// Polls <paramref name="getBufferedAmount"/> until it reaches zero (fully
    /// drained — returns <see langword="true"/>), or gives up and returns
    /// <see langword="false"/> once <paramref name="stallTimeout"/> has elapsed
    /// with no decrease in the observed value.
    /// </summary>
    /// <remarks>
    /// Extracted as a static method parameterized over time/delay so the
    /// stall-vs-fixed-timeout behavior can be unit tested deterministically,
    /// without relying on real wall-clock waits or real network throughput.
    /// This is the core fix for a regression where an earlier version of this
    /// method used a fixed absolute timeout (10s) that didn't scale to large
    /// files: a big file's send queue can legitimately take much longer than
    /// any fixed timeout to drain, so the only correct condition for giving up
    /// is "no progress for N seconds," not "N seconds have passed."
    /// </remarks>
    internal static async Task<bool> WaitForDrainOrStallAsync(
        Func<ulong> getBufferedAmount,
        TimeSpan pollInterval,
        TimeSpan stallTimeout,
        Func<TimeSpan, Task> delay,
        Func<DateTimeOffset> utcNow)
    {
        var lastValue = getBufferedAmount();
        var lastProgressAt = utcNow();

        while (getBufferedAmount() > 0)
        {
            await delay(pollInterval);

            var current = getBufferedAmount();
            if (current < lastValue)
            {
                lastValue = current;
                lastProgressAt = utcNow();
            }
            else if (utcNow() - lastProgressAt > stallTimeout)
            {
                return false;
            }
        }

        return true;
    }
}
