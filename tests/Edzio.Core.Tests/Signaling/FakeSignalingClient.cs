using Edzio.Core.Signaling;

namespace Edzio.Core.Tests.Signaling;

/// <summary>Test double for ISignalingClient. Routes messages in-memory.</summary>
public class FakeSignalingClient : ISignalingClient
{
    private readonly ConnectionStateTracker _tracker = new();

    public event EventHandler<string> OfferReceived = delegate { };
    public event EventHandler<string> AnswerReceived = delegate { };
    public event EventHandler<string> IceCandidateReceived = delegate { };
    public event EventHandler PeerJoined = delegate { };
    public event EventHandler PeerDisconnected = delegate { };

    public event EventHandler<SignalingConnectionState> ConnectionStateChanged
    {
        add => _tracker.Changed += value;
        remove => _tracker.Changed -= value;
    }

    public List<string> SentOffers { get; } = new();
    public List<string> SentAnswers { get; } = new();
    public List<string> SentIceCandidates { get; } = new();
    public bool Connected { get; private set; }
    public string? GeneratedCode { get; set; } = "ABCDEF";
    public bool JoinResult { get; set; } = true;

    /// <summary>When true, <see cref="ConnectAsync"/> throws instead of succeeding — for testing retry logic.</summary>
    public bool ThrowOnConnect { get; set; }

    public SignalingConnectionState ConnectionState => _tracker.State;

    // Routing hooks: allow PairedFakeSignaling to forward messages between peers
    public event Action<string>? OnOfferSent;
    public event Action<string>? OnAnswerSent;
    public event Action<string>? OnIceSent;

    public Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        if (ThrowOnConnect) throw new InvalidOperationException("Simulated connect failure");
        Connected = true;
        _tracker.TransitionTo(SignalingConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task<string> RegisterAsReceiverAsync(CancellationToken ct = default) => Task.FromResult(GeneratedCode ?? "XXXXXX");
    public Task<bool> JoinAsSenderAsync(string code, CancellationToken ct = default) => Task.FromResult(JoinResult);
    public Task SendOfferAsync(string sdp, CancellationToken ct = default) { SentOffers.Add(sdp); OnOfferSent?.Invoke(sdp); return Task.CompletedTask; }
    public Task SendAnswerAsync(string sdp, CancellationToken ct = default) { SentAnswers.Add(sdp); OnAnswerSent?.Invoke(sdp); return Task.CompletedTask; }
    public Task SendIceCandidateAsync(string c, CancellationToken ct = default) { SentIceCandidates.Add(c); OnIceSent?.Invoke(c); return Task.CompletedTask; }
    public Task WaitForConnectedAsync(CancellationToken ct = default) => _tracker.WaitForConnectedAsync(ct);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Simulation helpers
    public void SimulateOfferReceived(string sdp) => OfferReceived?.Invoke(this, sdp);
    public void SimulateAnswerReceived(string sdp) => AnswerReceived?.Invoke(this, sdp);
    public void SimulateIceCandidateReceived(string c) => IceCandidateReceived?.Invoke(this, c);
    public void SimulatePeerJoined() => PeerJoined?.Invoke(this, EventArgs.Empty);
    public void SimulatePeerDisconnected() => PeerDisconnected?.Invoke(this, EventArgs.Empty);

    /// <summary>Directly sets the connection state, for tests simulating SignalR-level
    /// transitions (Reconnecting, Disconnected) without a real connection.</summary>
    public void SetConnectionState(SignalingConnectionState state) => _tracker.TransitionTo(state);
}
