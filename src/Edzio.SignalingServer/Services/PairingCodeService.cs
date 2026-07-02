using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Edzio.SignalingServer.Services;

public class PairingCodeService : IPairingCodeService
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;
    private static readonly TimeSpan CodeExpiry = TimeSpan.FromMinutes(10);

    private record CodeEntry(string ReceiverConnectionId, DateTimeOffset CreatedAt);

    // code -> entry
    private readonly ConcurrentDictionary<string, CodeEntry> _codeToEntry = new();
    // receiverConnectionId -> code (for cleanup)
    private readonly ConcurrentDictionary<string, string> _connectionToCode = new();

    public string GenerateCode(string receiverConnectionId)
    {
        // Remove any existing code for this receiver
        RemoveConnection(receiverConnectionId);

        string code;
        do
        {
            code = GenerateRandomCode();
        } while (!_codeToEntry.TryAdd(code, new CodeEntry(receiverConnectionId, DateTimeOffset.UtcNow)));

        _connectionToCode[receiverConnectionId] = code;
        return code;
    }

    public bool TryJoin(string code, string senderConnectionId, out string? receiverConnectionId)
    {
        receiverConnectionId = null;

        if (!_codeToEntry.TryRemove(code, out var entry))
            return false;

        // Clean up the reverse mapping
        _connectionToCode.TryRemove(entry.ReceiverConnectionId, out _);

        if (DateTimeOffset.UtcNow - entry.CreatedAt > CodeExpiry)
            return false;

        receiverConnectionId = entry.ReceiverConnectionId;
        return true;
    }

    public void RemoveConnection(string connectionId)
    {
        if (_connectionToCode.TryRemove(connectionId, out var code))
        {
            _codeToEntry.TryRemove(code, out _);
        }
    }

    private static string GenerateRandomCode()
    {
        var chars = new char[CodeLength];
        for (int i = 0; i < CodeLength; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(chars);
    }
}
