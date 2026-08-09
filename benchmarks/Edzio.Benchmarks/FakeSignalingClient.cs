using Edzio.Core.Signaling;

namespace Edzio.Benchmarks;

/// <summary>Test double for ISignalingClient. Routes messages in-memory.</summary>
internal class FakeSignalingClient : ISignalingClient
{
    public event EventHandler<string> OfferReceived = delegate { };
    public event EventHandler<string> AnswerReceived = delegate { };
    public event EventHandler<string> IceCandidateReceived = delegate { };
    public event EventHandler PeerJoined = delegate { };
    public event EventHandler PeerDisconnected = delegate { };

    public event Action<string>? OnOfferSent;
    public event Action<string>? OnAnswerSent;
    public event Action<string>? OnIceSent;

    public Task ConnectAsync(string serverUrl, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> RegisterAsReceiverAsync(CancellationToken ct = default) => Task.FromResult("ABCDEF");
    public Task<bool> JoinAsSenderAsync(string code, CancellationToken ct = default) => Task.FromResult(true);
    public Task SendOfferAsync(string sdp, CancellationToken ct = default) { OnOfferSent?.Invoke(sdp); return Task.CompletedTask; }
    public Task SendAnswerAsync(string sdp, CancellationToken ct = default) { OnAnswerSent?.Invoke(sdp); return Task.CompletedTask; }
    public Task SendIceCandidateAsync(string c, CancellationToken ct = default) { OnIceSent?.Invoke(c); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void SimulateOfferReceived(string sdp) => OfferReceived.Invoke(this, sdp);
    public void SimulateAnswerReceived(string sdp) => AnswerReceived.Invoke(this, sdp);
    public void SimulateIceCandidateReceived(string c) => IceCandidateReceived.Invoke(this, c);
}

/// <summary>
/// Wires two FakeSignalingClients together so messages from one reach the
/// other, simulating the server relay without network I/O.
/// </summary>
internal class PairedFakeSignaling
{
    public FakeSignalingClient Offerer { get; } = new();
    public FakeSignalingClient Answerer { get; } = new();

    public PairedFakeSignaling()
    {
        Offerer.OnOfferSent += sdp => Answerer.SimulateOfferReceived(sdp);
        Offerer.OnAnswerSent += sdp => Answerer.SimulateAnswerReceived(sdp);
        Offerer.OnIceSent += c => Answerer.SimulateIceCandidateReceived(c);

        Answerer.OnOfferSent += sdp => Offerer.SimulateOfferReceived(sdp);
        Answerer.OnAnswerSent += sdp => Offerer.SimulateAnswerReceived(sdp);
        Answerer.OnIceSent += c => Offerer.SimulateIceCandidateReceived(c);
    }
}
