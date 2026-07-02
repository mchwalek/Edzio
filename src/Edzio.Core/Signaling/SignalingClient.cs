using Microsoft.AspNetCore.SignalR.Client;

namespace Edzio.Core.Signaling;

public sealed class SignalingClient : ISignalingClient
{
    private HubConnection? _connection;

    public event EventHandler<string> OfferReceived = delegate { };
    public event EventHandler<string> AnswerReceived = delegate { };
    public event EventHandler<string> IceCandidateReceived = delegate { };
    public event EventHandler PeerJoined = delegate { };
    public event EventHandler PeerDisconnected = delegate { };

    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(serverUrl.TrimEnd('/') + "/signaling")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<string>(SignalingEvents.OfferReceived, sdp => OfferReceived?.Invoke(this, sdp));
        _connection.On<string>(SignalingEvents.AnswerReceived, sdp => AnswerReceived?.Invoke(this, sdp));
        _connection.On<string>(SignalingEvents.IceCandidateReceived, c => IceCandidateReceived?.Invoke(this, c));
        _connection.On(SignalingEvents.PeerJoined, () => PeerJoined?.Invoke(this, EventArgs.Empty));
        _connection.On(SignalingEvents.PeerDisconnected, () => PeerDisconnected?.Invoke(this, EventArgs.Empty));

        await _connection.StartAsync(ct);
    }

    public Task<string> RegisterAsReceiverAsync(CancellationToken ct = default)
        => EnsureConnected().InvokeAsync<string>(SignalingMethods.RegisterReceiver, ct);

    public Task<bool> JoinAsSenderAsync(string code, CancellationToken ct = default)
        => EnsureConnected().InvokeAsync<bool>(SignalingMethods.JoinAsSender, code, ct);

    public Task SendOfferAsync(string sdp, CancellationToken ct = default)
        => EnsureConnected().InvokeAsync(SignalingMethods.SendOffer, sdp, ct);

    public Task SendAnswerAsync(string sdp, CancellationToken ct = default)
        => EnsureConnected().InvokeAsync(SignalingMethods.SendAnswer, sdp, ct);

    public Task SendIceCandidateAsync(string candidateJson, CancellationToken ct = default)
        => EnsureConnected().InvokeAsync(SignalingMethods.SendIceCandidate, candidateJson, ct);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    private HubConnection EnsureConnected()
        => _connection ?? throw new InvalidOperationException("Call ConnectAsync first.");
}
