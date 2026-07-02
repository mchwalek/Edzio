using Makaretu.Dns;
namespace Edzio.Core.Discovery;

public sealed class MdnsDiscovery : ILocalDiscovery {
    private const string ServiceType = "_edzio._tcp";
    private const int DefaultPort = 7777;
    private readonly int _port;
    private readonly string _displayName;
    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private readonly List<LocalPeer> _peers = new();
    private readonly object _lock = new();

    public IReadOnlyList<LocalPeer> DiscoveredPeers { get { lock(_lock) return _peers.ToList(); } }
    public event EventHandler<IReadOnlyList<LocalPeer>> PeersChanged = delegate { };

    public MdnsDiscovery(string? displayName = null, int port = DefaultPort) {
        _displayName = displayName ?? Environment.MachineName;
        _port = port;
    }

    public Task StartAsync(CancellationToken ct = default) {
        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);

        var profile = new ServiceProfile(_displayName, ServiceType, (ushort)_port);
        profile.AddProperty("displayName", _displayName);
        profile.AddProperty("version", "1");

        _sd.Advertise(profile);
        _sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        _sd.QueryAllServices();
        _mdns.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync() {
        _sd?.Dispose();
        _mdns?.Stop();
        _mdns?.Dispose();
        _sd = null;
        _mdns = null;
        return Task.CompletedTask;
    }

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e) {
        // Skip our own instance
        if (e.ServiceInstanceName.Labels[0] == _displayName) return;

        // Extract info from DNS message records
        string? ip = null;
        int port = DefaultPort;
        string? displayName = null;

        foreach (var record in e.Message.Answers.Concat(e.Message.AdditionalRecords)) {
            if (record is SRVRecord srv) port = srv.Port;
            if (record is TXTRecord txt) {
                foreach (var str in txt.Strings) {
                    if (str.StartsWith("displayName=")) displayName = str["displayName=".Length..];
                }
            }
            if (record is AddressRecord addr) ip = addr.Address.ToString();
        }

        if (ip is null) return;
        var peer = new LocalPeer(displayName ?? e.ServiceInstanceName.Labels[0], ip, port);

        lock (_lock) {
            _peers.RemoveAll(p => p.IpAddress == ip && p.Port == port);
            _peers.Add(peer);
        }
        PeersChanged?.Invoke(this, DiscoveredPeers);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
