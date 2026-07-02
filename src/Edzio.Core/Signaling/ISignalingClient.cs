namespace Edzio.Core.Signaling;

public interface ISignalingClient : IAsyncDisposable
{
    Task ConnectAsync(string serverUrl, CancellationToken ct = default);
    Task<string> RegisterAsReceiverAsync(CancellationToken ct = default);
    Task<bool> JoinAsSenderAsync(string code, CancellationToken ct = default);
    Task SendOfferAsync(string sdp, CancellationToken ct = default);
    Task SendAnswerAsync(string sdp, CancellationToken ct = default);
    Task SendIceCandidateAsync(string candidateJson, CancellationToken ct = default);

    event EventHandler<string> OfferReceived;
    event EventHandler<string> AnswerReceived;
    event EventHandler<string> IceCandidateReceived;
    event EventHandler PeerJoined;
    event EventHandler PeerDisconnected;
}
