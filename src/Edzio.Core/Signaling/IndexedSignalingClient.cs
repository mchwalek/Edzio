using System.Text.Json;

namespace Edzio.Core.Signaling;

/// <summary>
/// Routes signaling traffic for one of several parallel peer connections sharing a
/// single <see cref="ISignalingClient"/>.
/// </summary>
/// <remarks>
/// Outbound payloads are wrapped as <c>{"edzioLane":N,"payload":"..."}</c>; inbound
/// payloads are unwrapped and re-raised only when the lane index matches. The decorated
/// <see cref="WebRtcChannel"/> is unaware any of this is happening.
///
/// The envelope rides the existing relay methods unchanged: SignalingHub forwards these
/// strings blindly, exactly as it already does for the LAN endpoint advertisement, so
/// the deployed signaling server needs no change.
///
/// Inbound events that arrive before a subscriber attaches are buffered and replayed on
/// subscription. Without this, lane N's offer can be lost while lane 0 is still inside
/// its own connect sequence.
/// </remarks>
internal sealed class IndexedSignalingClient : ISignalingClient
{
    private const string LaneProperty = "edzioLane";
    private const string PayloadProperty = "payload";

    private readonly ISignalingClient _inner;
    private readonly int _laneIndex;
    private readonly object _gate = new();

    private EventHandler<string>? _offerReceived;
    private EventHandler<string>? _answerReceived;
    private EventHandler<string>? _iceCandidateReceived;

    private string? _pendingOffer;
    private string? _pendingAnswer;
    private readonly List<string> _pendingCandidates = [];

    /// <summary>
    /// Creates a lane view over a shared signaling client.
    /// </summary>
    /// <param name="inner">The shared client. Not owned — never disposed by this type.</param>
    /// <param name="laneIndex">This lane's index. Must match on both peers.</param>
    internal IndexedSignalingClient(ISignalingClient inner, int laneIndex)
    {
        _inner = inner;
        _laneIndex = laneIndex;

        _inner.OfferReceived += OnInnerOffer;
        _inner.AnswerReceived += OnInnerAnswer;
        _inner.IceCandidateReceived += OnInnerIceCandidate;
        _inner.PeerJoined += OnInnerPeerJoined;
        _inner.PeerDisconnected += OnInnerPeerDisconnected;
    }

    /// <inheritdoc />
    public Task ConnectAsync(string serverUrl, CancellationToken ct = default) =>
        _inner.ConnectAsync(serverUrl, ct);

    /// <inheritdoc />
    public Task<string> RegisterAsReceiverAsync(CancellationToken ct = default) =>
        _inner.RegisterAsReceiverAsync(ct);

    /// <inheritdoc />
    public Task<bool> JoinAsSenderAsync(string code, CancellationToken ct = default) =>
        _inner.JoinAsSenderAsync(code, ct);

    /// <inheritdoc />
    public Task SendOfferAsync(string sdp, CancellationToken ct = default) =>
        _inner.SendOfferAsync(Wrap(sdp), ct);

    /// <inheritdoc />
    public Task SendAnswerAsync(string sdp, CancellationToken ct = default) =>
        _inner.SendAnswerAsync(Wrap(sdp), ct);

    /// <inheritdoc />
    public Task SendIceCandidateAsync(string candidateJson, CancellationToken ct = default) =>
        _inner.SendIceCandidateAsync(Wrap(candidateJson), ct);

    /// <inheritdoc />
    public event EventHandler<string> OfferReceived
    {
        add
        {
            string? replay;
            lock (_gate)
            {
                _offerReceived += value;
                replay = _pendingOffer;
                _pendingOffer = null;
            }

            if (replay is not null) value(this, replay);
        }
        remove
        {
            lock (_gate) _offerReceived -= value;
        }
    }

    /// <inheritdoc />
    public event EventHandler<string> AnswerReceived
    {
        add
        {
            string? replay;
            lock (_gate)
            {
                _answerReceived += value;
                replay = _pendingAnswer;
                _pendingAnswer = null;
            }

            if (replay is not null) value(this, replay);
        }
        remove
        {
            lock (_gate) _answerReceived -= value;
        }
    }

    /// <inheritdoc />
    public event EventHandler<string> IceCandidateReceived
    {
        add
        {
            string[] replay;
            lock (_gate)
            {
                _iceCandidateReceived += value;
                replay = [.. _pendingCandidates];
                _pendingCandidates.Clear();
            }

            foreach (var candidate in replay) value(this, candidate);
        }
        remove
        {
            lock (_gate) _iceCandidateReceived -= value;
        }
    }

    // No payload to demultiplex, so these pass straight through to every lane.

    /// <inheritdoc />
    public event EventHandler PeerJoined = delegate { };

    /// <inheritdoc />
    public event EventHandler PeerDisconnected = delegate { };

    /// <summary>
    /// Detaches from the shared client. The shared client itself is left alive —
    /// it is owned by the caller and used by the other lanes.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _inner.OfferReceived -= OnInnerOffer;
        _inner.AnswerReceived -= OnInnerAnswer;
        _inner.IceCandidateReceived -= OnInnerIceCandidate;
        _inner.PeerJoined -= OnInnerPeerJoined;
        _inner.PeerDisconnected -= OnInnerPeerDisconnected;
        return ValueTask.CompletedTask;
    }

    private string Wrap(string payload) =>
        JsonSerializer.Serialize(new Envelope(_laneIndex, payload));

    /// <summary>
    /// Returns the inner payload if this message belongs to this lane, otherwise null.
    /// Non-envelope messages — notably the LAN endpoint advertisement — return null and
    /// are dropped rather than misrouted.
    /// </summary>
    private string? Unwrap(string wire)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(wire);
            if (envelope is null || envelope.Payload is null) return null;
            return envelope.Lane == _laneIndex ? envelope.Payload : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void OnInnerOffer(object? sender, string wire)
    {
        if (Unwrap(wire) is not { } payload) return;

        EventHandler<string>? handler;
        lock (_gate)
        {
            handler = _offerReceived;
            if (handler is null)
            {
                _pendingOffer = payload;
                return;
            }
        }

        handler(this, payload);
    }

    private void OnInnerAnswer(object? sender, string wire)
    {
        if (Unwrap(wire) is not { } payload) return;

        EventHandler<string>? handler;
        lock (_gate)
        {
            handler = _answerReceived;
            if (handler is null)
            {
                _pendingAnswer = payload;
                return;
            }
        }

        handler(this, payload);
    }

    private void OnInnerIceCandidate(object? sender, string wire)
    {
        if (Unwrap(wire) is not { } payload) return;

        EventHandler<string>? handler;
        lock (_gate)
        {
            handler = _iceCandidateReceived;
            if (handler is null)
            {
                _pendingCandidates.Add(payload);
                return;
            }
        }

        handler(this, payload);
    }

    private void OnInnerPeerJoined(object? sender, EventArgs e) => PeerJoined(this, e);

    private void OnInnerPeerDisconnected(object? sender, EventArgs e) => PeerDisconnected(this, e);

    private sealed record Envelope(
        [property: System.Text.Json.Serialization.JsonPropertyName(LaneProperty)] int Lane,
        [property: System.Text.Json.Serialization.JsonPropertyName(PayloadProperty)] string? Payload);
}
