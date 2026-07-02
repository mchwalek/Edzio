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
    /// Maximum time to wait for the outbound SCTP send queue to drain before
    /// forcibly closing the connection in <see cref="DisposeAsync"/>.
    /// </summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

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

        // Log ICE + connection state transitions — critical for diagnosing hangs
        _pc.oniceconnectionstatechange += state =>
            Log($"[{_role}] ICE connection state → {state}");
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

    /// <inheritdoc/>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        await WaitForOpenAsync(ct);
        _dataChannel!.send(data);
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
    /// </remarks>
    private async Task FlushOutboundDataAsync()
    {
        if (_dataChannel is null || _dataChannel.readyState != RTCDataChannelState.open)
            return;

        var deadline = DateTime.UtcNow + FlushTimeout;
        while (_dataChannel.bufferedAmount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (_dataChannel.bufferedAmount > 0)
        {
            Log($"[{_role}] Timed out after {FlushTimeout.TotalSeconds}s waiting for outbound data " +
                $"to flush ({_dataChannel.bufferedAmount} bytes still buffered) — closing anyway.");
        }
        else
        {
            // Give the last fragment(s) a brief moment to actually leave the socket.
            await Task.Delay(FlushGracePeriod);
        }
    }
}
