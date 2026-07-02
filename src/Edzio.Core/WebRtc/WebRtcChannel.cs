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
        _pc = new RTCPeerConnection(_rtcConfig);

        // Log ICE + connection state transitions — critical for diagnosing hangs
        _pc.oniceconnectionstatechange += state =>
            Log($"[{_role}] ICE connection state → {state}");
        _pc.onconnectionstatechange += state =>
            Log($"[{_role}] Peer connection state → {state}");
        _pc.onsignalingstatechange += () =>
            Log($"[{_role}] Signaling state → {_pc?.signalingState}");
        _pc.onicegatheringstatechange += state =>
            Log($"[{_role}] ICE gathering state → {state}");

        // ── ICE candidate queue ─────────────────────────────────────────
        // Remote candidates may arrive before setRemoteDescription is called.
        // Buffer them and flush once the remote description is in place.
        var pendingCandidates = new List<RTCIceCandidateInit>();
        var remoteDescSet = false;
        var candidateLock = new object();
        var localCandidateCount = 0;
        var remoteCandidateCount = 0;

        // ── Forward our ICE candidates to the remote peer ───────────────
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

        // ── Buffer remote ICE candidates until remote description is set ─
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

        // ── Apply remote description and flush buffered candidates ───────
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

        // ── Helper to wire a data channel once we have it ───────────────
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
        }

        if (_role == WebRtcRole.Offerer)
        {
            // Subscribe to AnswerReceived BEFORE sending the offer.
            // On a fast LAN the answer can arrive before control returns from
            // SendOfferAsync — if we subscribed after, we'd miss it entirely.
            var answerTcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _signaling.AnswerReceived += (_, sdp) =>
            {
                Log($"[{_role}] Answer received from signaling");
                answerTcs.TrySetResult(sdp);
            };

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

            // Subscribe to OfferReceived before anything else — same race applies.
            var offerTcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _signaling.OfferReceived += (_, sdp) =>
            {
                Log($"[{_role}] Offer received from signaling");
                offerTcs.TrySetResult(sdp);
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
    public ValueTask DisposeAsync()
    {
        _pc?.close();
        _pc?.Dispose();
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
