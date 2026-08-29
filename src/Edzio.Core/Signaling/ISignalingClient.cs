namespace Edzio.Core.Signaling;

public interface ISignalingClient : IAsyncDisposable
{
    Task ConnectAsync(string serverUrl, CancellationToken ct = default);
    Task<string> RegisterAsReceiverAsync(CancellationToken ct = default);
    Task<bool> JoinAsSenderAsync(string code, CancellationToken ct = default);
    Task SendOfferAsync(string sdp, CancellationToken ct = default);
    Task SendAnswerAsync(string sdp, CancellationToken ct = default);
    Task SendIceCandidateAsync(string candidateJson, CancellationToken ct = default);

    /// <summary>The current signaling connection lifecycle state.</summary>
    SignalingConnectionState ConnectionState { get; }

    /// <summary>Raised whenever <see cref="ConnectionState"/> changes.</summary>
    event EventHandler<SignalingConnectionState> ConnectionStateChanged;

    /// <summary>
    /// Completes immediately if <see cref="ConnectionState"/> is already Connected;
    /// otherwise completes on the next transition to Connected.
    /// </summary>
    Task WaitForConnectedAsync(CancellationToken ct = default);

    event EventHandler<string> OfferReceived;
    event EventHandler<string> AnswerReceived;
    event EventHandler<string> IceCandidateReceived;
    event EventHandler PeerJoined;
    event EventHandler PeerDisconnected;
}
