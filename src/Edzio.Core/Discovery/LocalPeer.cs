namespace Edzio.Core.Discovery;

/// <summary>Represents a discovered Edzio peer on the local network.</summary>
/// <param name="DisplayName">The peer's human-readable name.</param>
/// <param name="IpAddresses">All of the peer's advertised IPv4/IPv6 addresses. Some may be unroutable (virtual adapters, link-local) — callers should race all of them, as <see cref="Lan.LanDirect.TryConnectAsync"/> does.</param>
/// <param name="Port">The peer's LAN listener port.</param>
/// <param name="InstanceId">A random identifier unique to that peer's running instance, used for self-exclusion and to look the peer back up by identity.</param>
/// <param name="CertSha256Hex">SHA-256 fingerprint (hex) of the peer's ephemeral TLS certificate, used to connect via <see cref="Lan.LanDirect"/>.</param>
/// <param name="TokenBase64">Base64-encoded one-time auth token for the peer's LAN listener.</param>
public record LocalPeer(
    string DisplayName,
    IReadOnlyList<string> IpAddresses,
    int Port,
    string InstanceId = "",
    string CertSha256Hex = "",
    string TokenBase64 = "")
{
    // IReadOnlyList<string> has no structural equality by default (two equal-content lists from
    // different sources would compare unequal), so compare/hash IpAddresses by content.
    /// <inheritdoc />
    public virtual bool Equals(LocalPeer? other) =>
        other is not null &&
        DisplayName == other.DisplayName &&
        IpAddresses.SequenceEqual(other.IpAddresses) &&
        Port == other.Port &&
        InstanceId == other.InstanceId &&
        CertSha256Hex == other.CertSha256Hex &&
        TokenBase64 == other.TokenBase64;

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DisplayName);
        foreach (var ip in IpAddresses) hash.Add(ip);
        hash.Add(Port);
        hash.Add(InstanceId);
        hash.Add(CertSha256Hex);
        hash.Add(TokenBase64);
        return hash.ToHashCode();
    }
}
