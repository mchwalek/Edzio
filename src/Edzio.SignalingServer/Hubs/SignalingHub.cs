using System.Collections.Concurrent;
using Edzio.SignalingServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace Edzio.SignalingServer.Hubs;

public class SignalingHub : Hub
{
    // connectionId -> partnerConnectionId, stored in both directions
    private static readonly ConcurrentDictionary<string, string> _partners = new();

    private readonly IPairingCodeService _pairingCodeService;

    public SignalingHub(IPairingCodeService pairingCodeService)
    {
        _pairingCodeService = pairingCodeService;
    }

    public string RegisterReceiver()
    {
        return _pairingCodeService.GenerateCode(Context.ConnectionId);
    }

    public async Task<bool> JoinAsSender(string code)
    {
        if (!_pairingCodeService.TryJoin(code, Context.ConnectionId, out var receiverConnectionId)
            || receiverConnectionId is null)
        {
            return false;
        }

        // Register partner mapping both ways
        _partners[Context.ConnectionId] = receiverConnectionId;
        _partners[receiverConnectionId] = Context.ConnectionId;

        await Clients.Client(receiverConnectionId).SendAsync("PeerJoined");
        return true;
    }

    public async Task SendOffer(string sdp)
    {
        if (_partners.TryGetValue(Context.ConnectionId, out var partner))
        {
            await Clients.Client(partner).SendAsync("OfferReceived", sdp);
        }
    }

    public async Task SendAnswer(string sdp)
    {
        if (_partners.TryGetValue(Context.ConnectionId, out var partner))
        {
            await Clients.Client(partner).SendAsync("AnswerReceived", sdp);
        }
    }

    public async Task SendIceCandidate(string candidateJson)
    {
        if (_partners.TryGetValue(Context.ConnectionId, out var partner))
        {
            await Clients.Client(partner).SendAsync("IceCandidateReceived", candidateJson);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _pairingCodeService.RemoveConnection(Context.ConnectionId);

        if (_partners.TryRemove(Context.ConnectionId, out var partner))
        {
            // Remove the reverse mapping too
            _partners.TryRemove(partner, out _);
            await Clients.Client(partner).SendAsync("PeerDisconnected");
        }

        await base.OnDisconnectedAsync(exception);
    }
}
