using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Edzio.Core.Signaling;

public sealed class SignalingClient : ISignalingClient
{
    private HubConnection? _connection;
    private readonly ILogger<SignalingClient>? _logger;

    public event EventHandler<string> OfferReceived = delegate { };
    public event EventHandler<string> AnswerReceived = delegate { };
    public event EventHandler<string> IceCandidateReceived = delegate { };
    public event EventHandler PeerJoined = delegate { };
    public event EventHandler PeerDisconnected = delegate { };

    public SignalingClient(ILogger<SignalingClient>? logger = null) => _logger = logger;

    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        _logger?.LogInformation("Connecting to {Url}", serverUrl.TrimEnd('/') + "/signaling");
        _connection = new HubConnectionBuilder()
            .WithUrl(serverUrl.TrimEnd('/') + "/signaling")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<string>(SignalingEvents.OfferReceived, sdp =>
        {
            _logger?.LogInformation("← OfferReceived (sdp length={Len})", sdp.Length);
            OfferReceived?.Invoke(this, sdp);
        });
        _connection.On<string>(SignalingEvents.AnswerReceived, sdp =>
        {
            _logger?.LogInformation("← AnswerReceived (sdp length={Len})", sdp.Length);
            AnswerReceived?.Invoke(this, sdp);
        });
        _connection.On<string>(SignalingEvents.IceCandidateReceived, c =>
        {
            _logger?.LogInformation("← IceCandidateReceived: {C}", c);
            IceCandidateReceived?.Invoke(this, c);
        });
        _connection.On(SignalingEvents.PeerJoined, () =>
        {
            _logger?.LogInformation("← PeerJoined");
            PeerJoined?.Invoke(this, EventArgs.Empty);
        });
        _connection.On(SignalingEvents.PeerDisconnected, () =>
        {
            _logger?.LogInformation("← PeerDisconnected");
            PeerDisconnected?.Invoke(this, EventArgs.Empty);
        });

        _connection.Reconnecting += ex => { _logger?.LogWarning("SignalR reconnecting: {Ex}", ex?.Message); return Task.CompletedTask; };
        _connection.Reconnected += id => { _logger?.LogInformation("SignalR reconnected, connectionId={Id}", id); return Task.CompletedTask; };
        _connection.Closed += ex => { _logger?.LogWarning("SignalR connection closed: {Ex}", ex?.Message); return Task.CompletedTask; };

        await _connection.StartAsync(ct);
        _logger?.LogInformation("SignalR connected, connectionId={Id}", _connection.ConnectionId);
    }

    public Task SendOfferAsync(string sdp, CancellationToken ct = default)
    {
        _logger?.LogInformation("→ SendOffer (sdp length={Len})", sdp.Length);
        return EnsureConnected().InvokeAsync(SignalingMethods.SendOffer, sdp, ct);
    }

    public Task SendAnswerAsync(string sdp, CancellationToken ct = default)
    {
        _logger?.LogInformation("→ SendAnswer (sdp length={Len})", sdp.Length);
        return EnsureConnected().InvokeAsync(SignalingMethods.SendAnswer, sdp, ct);
    }

    public Task SendIceCandidateAsync(string candidateJson, CancellationToken ct = default)
    {
        _logger?.LogInformation("→ SendIceCandidate: {C}", candidateJson);
        return EnsureConnected().InvokeAsync(SignalingMethods.SendIceCandidate, candidateJson, ct);
    }

    public Task<string> RegisterAsReceiverAsync(CancellationToken ct = default)
        => EnsureConnected().InvokeAsync<string>(SignalingMethods.RegisterReceiver, ct);

    public Task<bool> JoinAsSenderAsync(string code, CancellationToken ct = default)
        => EnsureConnected().InvokeAsync<bool>(SignalingMethods.JoinAsSender, code, ct);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    private HubConnection EnsureConnected()
        => _connection ?? throw new InvalidOperationException("Call ConnectAsync first.");
}
