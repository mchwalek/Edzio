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
    /// Starts the signalling exchange. Does not wait for the data channel
    /// to open — call <see cref="WaitForOpenAsync"/> for that.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _pc = new RTCPeerConnection(_rtcConfig);

        // ── Forward our ICE candidates to the remote peer ───────────────
        _pc.onicecandidate += candidate =>
        {
            var json = JsonSerializer.Serialize(new
            {
                candidate = candidate.candidate,
                sdpMid = candidate.sdpMid,
                sdpMLineIndex = candidate.sdpMLineIndex
            });
            // Fire-and-forget: signaling send is best-effort
            _ = _signaling.SendIceCandidateAsync(json);
        };

        // ── Apply ICE candidates received from the remote peer ──────────
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
                _pc?.addIceCandidate(init);  // synchronous in SIPSorcery 6.x
            }
            catch
            {
                // Ignore malformed candidate JSON
            }
        };

        // ── Helper to wire a data channel once we have it ───────────────
        void WireDataChannel(RTCDataChannel dc)
        {
            _dataChannel = dc;
            dc.onopen += () => _channelOpen.TrySetResult();
            // onmessage: (RTCDataChannel, DataChannelPayloadProtocols, byte[])
            dc.onmessage += (_, _, data) => _incoming.Writer.TryWrite(data);
        }

        if (_role == WebRtcRole.Offerer)
        {
            // ── Offerer: create channel → offer → send offer ─────────────
            var dc = await _pc.createDataChannel("edzio");
            WireDataChannel(dc);

            var offer = _pc.createOffer();                   // synchronous
            await _pc.setLocalDescription(offer);
            await _signaling.SendOfferAsync(offer.sdp);

            // Subscribe AFTER sending so it never races with the send
            _signaling.AnswerReceived += (_, sdp) =>
            {
                // setRemoteDescription is synchronous in SIPSorcery 6.x
                _pc?.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = sdp
                });
            };
        }
        else
        {
            // ── Answerer: wait for offer → answer ────────────────────────
            _pc.ondatachannel += dc => WireDataChannel(dc);

            _signaling.OfferReceived += (_, sdp) =>
            {
                if (_pc is null) return;

                _pc.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.offer,
                    sdp = sdp
                });

                var answer = _pc.createAnswer();            // synchronous

                // setLocalDescription is async; fire-and-forget from event
                _ = Task.Run(async () =>
                {
                    await _pc.setLocalDescription(answer);
                    await _signaling.SendAnswerAsync(answer.sdp);
                });
            };
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
