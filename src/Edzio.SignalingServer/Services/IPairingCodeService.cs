namespace Edzio.SignalingServer.Services;

public interface IPairingCodeService
{
    string GenerateCode(string receiverConnectionId);
    bool TryJoin(string code, string senderConnectionId, out string? receiverConnectionId);
    void RemoveConnection(string connectionId);
}
