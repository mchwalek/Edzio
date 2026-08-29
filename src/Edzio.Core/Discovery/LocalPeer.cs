namespace Edzio.Core.Discovery;

/// <summary>Represents a discovered Edzio peer on the local network.</summary>
/// <param name="DisplayName">The peer's human-readable name.</param>
/// <param name="IpAddress">The peer's IPv4/IPv6 address.</param>
/// <param name="Port">The peer's LAN listener port.</param>
/// <param name="InstanceId">A random identifier unique to that peer's running instance, used for self-exclusion and to look the peer back up by identity.</param>
/// <param name="CertSha256Hex">SHA-256 fingerprint (hex) of the peer's ephemeral TLS certificate, used to connect via <see cref="Lan.LanDirect"/>.</param>
/// <param name="TokenBase64">Base64-encoded one-time auth token for the peer's LAN listener.</param>
public record LocalPeer(
    string DisplayName,
    string IpAddress,
    int Port,
    string InstanceId = "",
    string CertSha256Hex = "",
    string TokenBase64 = "");
