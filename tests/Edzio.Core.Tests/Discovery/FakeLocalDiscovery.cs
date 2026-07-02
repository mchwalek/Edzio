namespace Edzio.Core.Tests.Discovery;
using Edzio.Core.Discovery;
public class FakeLocalDiscovery : ILocalDiscovery {
    private readonly List<LocalPeer> _peers = new();
    public IReadOnlyList<LocalPeer> DiscoveredPeers => _peers.AsReadOnly();
    public event EventHandler<IReadOnlyList<LocalPeer>> PeersChanged = delegate { };
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }

    public Task StartAsync(CancellationToken ct = default) { Started = true; return Task.CompletedTask; }
    public Task StopAsync() { Stopped = true; return Task.CompletedTask; }
    public void SimulateDiscovery(LocalPeer peer) {
        _peers.Add(peer);
        PeersChanged?.Invoke(this, _peers.AsReadOnly());
    }
    public void SimulateRemoval(LocalPeer peer) {
        _peers.Remove(peer);
        PeersChanged?.Invoke(this, _peers.AsReadOnly());
    }
    public ValueTask DisposeAsync() { Stopped = true; return ValueTask.CompletedTask; }
}
