using Edzio.Core.Signaling;
using Edzio.Core.Transfer;
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

    private RTCPeerConnection? _pc;
    private RTCDataChannel? _dataChannel;

    private readonly Channel<byte[]> _incoming =
        Channel.CreateBounded<byte[]>(64);

    private readonly TaskCompletionSource _channelOpen =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WebRtcChannel(
        RTCConfiguration rtcConfig,
        ISignalingClient signaling,
        WebRtcRole role)
    {
        _rtcConfig = rtcConfig;
        _signaling = signaling;
        _role = role;
    }

    /// <summary>
    /// Performs the full signalling exchange (offer/answer + ICE) and returns once
    /// both sides have set their remote descriptions. The data channel may still be
    /// opening; call <see cref="WaitForOpenAsync"/> before sending data.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _pc = new RTCPeerConnection(_rtcConfig);

        // ── ICE candidate queue ─────────────────────────────────────────
        // Remote candidates may arrive before setRemoteDescription is called.
        // Buffer them and flush once the remote description is in place.
        var pendingCandidates = new List<RTCIceCandidateInit>();
        var remoteDescSet = false;
        var candidateLock = new object();

        // ── Forward our ICE candidates to the remote peer ───────────────
        _pc.onicecandidate += candidate =>
        {
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
                lock (candidateLock)
                {
                    if (remoteDescSet)
                        _pc?.addIceCandidate(init);
                    else
                        pendingCandidates.Add(init);
                }
            }
            catch { /* Ignore malformed candidate JSON */ }
        };

        // ── Apply remote description and flush buffered candidates ───────
        void ApplyRemoteDescription(RTCSessionDescriptionInit desc)
        {
            _pc?.setRemoteDescription(desc);
            lock (candidateLock)
            {
                remoteDescSet = true;
                foreach (var c in pendingCandidates)
                    _pc?.addIceCandidate(c);
                pendingCandidates.Clear();
            }
        }

        // ── Helper to wire a data channel once we have it ───────────────
        void WireDataChannel(RTCDataChannel dc)
        {
            _dataChannel = dc;
            dc.onopen += () => _channelOpen.TrySetResult();
            dc.onmessage += (_, _, data) => _incoming.Writer.TryWrite(data);
        }

        if (_role == WebRtcRole.Offerer)
        {
            // Subscribe to AnswerReceived BEFORE sending the offer.
            // On a fast LAN the answer can arrive before control returns from
            // SendOfferAsync — if we subscribed after, we'd miss it entirely.
            var answerTcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _signaling.AnswerReceived += (_, sdp) => answerTcs.TrySetResult(sdp);

            var dc = await _pc.createDataChannel("edzio");
            WireDataChannel(dc);

            var offer = _pc.createOffer();
            await _pc.setLocalDescription(offer);
            await _signaling.SendOfferAsync(offer.sdp);

            var answerSdp = await answerTcs.Task.WaitAsync(ct);
            ApplyRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answerSdp
            });
        }
        else
        {
            _pc.ondatachannel += dc => WireDataChannel(dc);

            // Subscribe to OfferReceived before anything else — same race applies.
            var offerTcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _signaling.OfferReceived += (_, sdp) => offerTcs.TrySetResult(sdp);

            var offerSdp = await offerTcs.Task.WaitAsync(ct);
            ApplyRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offerSdp
            });

            var answer = _pc.createAnswer();
            await _pc.setLocalDescription(answer);
            await _signaling.SendAnswerAsync(answer.sdp);
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
