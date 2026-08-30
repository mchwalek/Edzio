using System.Threading;
using Makaretu.Dns;

namespace Edzio.Core.Discovery;

/// <summary>Discovers and advertises Edzio peers on the local network via mDNS.</summary>
public sealed class MdnsDiscovery : ILocalDiscovery
{
    private const string ServiceType = "_edzio._tcp";
    private const int DefaultPort = 7777;
    private readonly string _displayName;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, LocalPeer> _peers = new();
    private readonly object _lock = new();

    private int _advertisedPort = DefaultPort;
    private string _certSha256Hex = "";
    private string _tokenBase64 = "";

    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private Timer? _queryTimer;
    private volatile bool _foundFirstPeer;

    private static readonly DomainName QueryName = new($"{ServiceType}.local");

    /// <summary>Random identifier for this running instance, used for self-exclusion when discovering peers.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public IReadOnlyList<LocalPeer> DiscoveredPeers { get { lock (_lock) return _peers.Values.ToList(); } }

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<LocalPeer>> PeersChanged = delegate { };

    /// <summary>Creates a new mDNS discovery instance.</summary>
    /// <param name="displayName">The name to advertise for this instance; defaults to the machine name.</param>
    /// <param name="port">The LAN listener port to advertise.</param>
    /// <param name="log">Optional sink for diagnostic log lines (advertise/query/discovery/shutdown events).</param>
    public MdnsDiscovery(string? displayName = null, int port = DefaultPort, Action<string>? log = null)
    {
        _displayName = displayName ?? Environment.MachineName;
        _advertisedPort = port;
        _log = log;
    }

    /// <summary>Configures the LAN endpoint this instance advertises. Call before <see cref="StartAsync"/>.</summary>
    /// <param name="port">The real, connectable TCP port of this instance's LAN listener.</param>
    /// <param name="certSha256Hex">SHA-256 fingerprint (hex) of this instance's ephemeral TLS certificate.</param>
    /// <param name="tokenBase64">Base64-encoded one-time auth token for this instance's LAN listener.</param>
    public void SetAdvertisement(int port, string certSha256Hex, string tokenBase64)
    {
        _advertisedPort = port;
        _certSha256Hex = certSha256Hex;
        _tokenBase64 = tokenBase64;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);
        var profile = new ServiceProfile(_displayName, ServiceType, (ushort)_advertisedPort);
        profile.AddProperty("displayName", _displayName);
        profile.AddProperty("version", "1");
        profile.AddProperty("instanceId", InstanceId);
        profile.AddProperty("certSha256", _certSha256Hex);
        profile.AddProperty("token", _tokenBase64);
        _sd.Advertise(profile);
        _log?.Invoke($"Advertising '{_displayName}' ({InstanceId}) on port {_advertisedPort}");
        _sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        _sd.ServiceInstanceShutdown += OnServiceInstanceShutdown;
        _mdns.NetworkInterfaceDiscovered += (_, _) =>
        {
            _log?.Invoke("Network interfaces changed, re-querying");
            SendInstanceQuery();
        };
        _mdns.Start(); // Must start before querying — Start() discovers network interfaces and sets the max packet size

        // Query the _edzio._tcp instance directly (not QueryAllServices, which queries the
        // DNS-SD meta-service and takes far longer to resolve down to real instances).
        // Repeat every 3s until a peer answers, then stop — mDNS TTL/goodbye packets handle removals.
        SendInstanceQuery();
        _queryTimer = new Timer(_ => SendInstanceQuery(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        return Task.CompletedTask;
    }

    private void SendInstanceQuery()
    {
        if (_mdns is null) return;
        _log?.Invoke($"Sending PTR query for {QueryName}");
        _mdns.SendQuery(QueryName, type: DnsType.PTR);
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        _log?.Invoke("Stopping discovery");
        _queryTimer?.Dispose();
        _queryTimer = null;
        _sd?.Dispose();
        _mdns?.Stop();
        _mdns?.Dispose();
        _sd = null;
        _mdns = null;
        return Task.CompletedTask;
    }

    internal void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        if (!IsEdzioInstance(e.ServiceInstanceName, out var key)) return;

        var addresses = new List<string>();
        int port = DefaultPort;
        string? displayName = null;
        string instanceId = "";
        string certSha256Hex = "";
        string tokenBase64 = "";
        foreach (var record in e.Message.Answers.Concat(e.Message.AdditionalRecords))
        {
            if (record is SRVRecord srv) port = srv.Port;
            if (record is TXTRecord txt)
            {
                foreach (var str in txt.Strings)
                {
                    if (str.StartsWith("displayName=")) displayName = str["displayName=".Length..];
                    else if (str.StartsWith("instanceId=")) instanceId = str["instanceId=".Length..];
                    else if (str.StartsWith("certSha256=")) certSha256Hex = str["certSha256=".Length..];
                    else if (str.StartsWith("token=")) tokenBase64 = str["token=".Length..];
                }
            }
            // Collect every advertised address (not just the last one) — the peer may
            // advertise several interfaces, some of which may be unroutable from here;
            // LanDirect.TryConnectAsync races all of them and picks whichever connects.
            if (record is AddressRecord addr) addresses.Add(addr.Address.ToString());
        }
        if (addresses.Count == 0) return;
        if (instanceId == InstanceId) return; // self

        _log?.Invoke($"Discovered '{displayName ?? key}' ({instanceId}) at [{string.Join(", ", addresses)}]:{port}");

        var peer = new LocalPeer(displayName ?? key, addresses, port, instanceId, certSha256Hex, tokenBase64);
        lock (_lock) { _peers[key] = peer; }
        PeersChanged?.Invoke(this, DiscoveredPeers);

        if (!_foundFirstPeer)
        {
            _foundFirstPeer = true;
            _log?.Invoke("First peer found, stopping periodic re-query");
            _queryTimer?.Dispose();
            _queryTimer = null;
        }
    }

    internal void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        if (!IsEdzioInstance(e.ServiceInstanceName, out var key)) return;

        bool removed;
        lock (_lock) { removed = _peers.Remove(key); }
        if (removed)
        {
            _log?.Invoke($"Peer '{key}' shut down / left the network");
            PeersChanged?.Invoke(this, DiscoveredPeers);
        }
    }

    private static readonly string ServiceInstanceSuffix = $"._{ServiceType.TrimStart('_')}.local";

    private static bool IsEdzioInstance(DomainName serviceInstanceName, out string key)
    {
        key = serviceInstanceName.ToString();
        return key.EndsWith(ServiceInstanceSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync();
}
