namespace Edzio.Core.Discovery;
public interface ILocalDiscovery : IAsyncDisposable {
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    IReadOnlyList<LocalPeer> DiscoveredPeers { get; }
    event EventHandler<IReadOnlyList<LocalPeer>> PeersChanged;
}
