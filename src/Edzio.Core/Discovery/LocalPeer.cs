namespace Edzio.Core.Discovery;
/// <summary>Represents a discovered Edzio peer on the local network.</summary>
public record LocalPeer(string DisplayName, string IpAddress, int Port);
