using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Edzio.Core.Lan;

/// <summary>
/// The receiver's LAN listening endpoint, relayed to the sender over the
/// signaling channel (piggybacked on the ICE-candidate relay so the deployed
/// signaling server needs no changes). The TLS certificate fingerprint and
/// one-time token make the connection as trustworthy as the signaling channel
/// itself — the same trust model WebRTC uses for its DTLS fingerprints in SDP.
/// </summary>
public sealed record LanEndpointAdvertisement(
    [property: JsonPropertyName("addresses")] IReadOnlyList<string> Addresses,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("token")] string TokenBase64,
    [property: JsonPropertyName("certSha256")] string CertSha256Hex);

/// <summary>
/// Establishes LAN-direct TLS connections between two Edzio peers:
/// the receiver listens (<see cref="LanDirectListener"/>), advertises its
/// endpoint via signaling, and the sender connects (<see cref="TryConnectAsync"/>).
/// </summary>
public static class LanDirect
{
    /// <summary>JSON property name that marks a relayed signaling message as a LAN endpoint advertisement.</summary>
    public const string AdvertisementJsonKey = "edzioLanEndpoint";

    private const int TokenLengthBytes = 32;

    /// <summary>Wraps an advertisement in the signaling relay envelope: <c>{"edzioLanEndpoint":{...}}</c>.</summary>
    public static string SerializeAdvertisement(LanEndpointAdvertisement ad)
        => JsonSerializer.Serialize(new Dictionary<string, LanEndpointAdvertisement> { [AdvertisementJsonKey] = ad });

    /// <summary>
    /// Returns the advertisement if <paramref name="json"/> is a LAN endpoint
    /// advertisement envelope; null for anything else (e.g. a real ICE candidate).
    /// </summary>
    public static LanEndpointAdvertisement? TryParseAdvertisement(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(AdvertisementJsonKey, out var inner))
                return null;
            return inner.Deserialize<LanEndpointAdvertisement>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// All plausible LAN unicast addresses of this machine: up interfaces,
    /// excluding loopback, IPv6 link-local (scope-id headaches), and IPv4
    /// APIPA (169.254.x.x).
    /// </summary>
    public static IReadOnlyList<IPAddress> GatherLanAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = addr.Address;
                if (ip.IsIPv6LinkLocal) continue;
                if (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is [169, 254, ..]) continue;
                result.Add(ip);
            }
        }
        return result;
    }

    /// <summary>
    /// Attempts a LAN-direct connection to an advertised endpoint: races a TCP
    /// connect to every advertised address, then performs the TLS handshake
    /// (validating the certificate strictly by its advertised SHA-256
    /// fingerprint) and sends the one-time auth token. Returns null on any
    /// failure or timeout — the caller falls back to WebRTC.
    /// </summary>
    /// <param name="log">
    /// Optional diagnostic sink for per-address connect outcomes — added
    /// because LAN-direct was observed to intermittently fall back to WebRTC
    /// with no visibility into which address(es) failed or why (firewall drop,
    /// stale/rotated IPv6 temporary address, or simple timeout) — see
    /// docs/debug/slow-webrtc-transfer-throughput session 6.
    /// </param>
    public static async Task<TcpTransferChannel?> TryConnectAsync(
        LanEndpointAdvertisement ad, TimeSpan timeout, Action<string>? log = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var client = await ConnectFirstAsync(ad, log, cts.Token);
            if (client is null)
            {
                log?.Invoke($"[LanDirect] No address connected within {timeout.TotalSeconds:F1}s — giving up.");
                return null;
            }

            var connectedEndpoint = client.Client.RemoteEndPoint;
            try
            {
                client.NoDelay = true;
                var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "edzio-lan",
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        cert is not null &&
                        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))
                            .Equals(ad.CertSha256Hex, StringComparison.OrdinalIgnoreCase)
                }, cts.Token);

                await ssl.WriteAsync(Convert.FromBase64String(ad.TokenBase64), cts.Token);
                await ssl.FlushAsync(cts.Token);

                log?.Invoke($"[LanDirect] Connected to {connectedEndpoint} and authenticated in {sw.ElapsedMilliseconds}ms.");
                return new TcpTransferChannel(client, ssl);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[LanDirect] TLS handshake/auth to {connectedEndpoint} failed after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
                client.Dispose();
                throw;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Timeout, refused, TLS failure, bad advertisement — all mean
            // "LAN direct not available"; the caller falls back to WebRTC.
            return null;
        }
    }

    private static async Task<TcpClient?> ConnectFirstAsync(
        LanEndpointAdvertisement ad, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"[LanDirect] Racing TCP connect to {ad.Addresses.Count} address(es): " +
            $"{string.Join(", ", ad.Addresses)} (port {ad.Port})");

        // A separate CTS for just the connect race (linked to, but distinct from,
        // the caller's overall-call token): cancelling it the moment a winner is
        // chosen stops the remaining attempts immediately, rather than leaving
        // them to linger until the whole TryConnectAsync call (including the
        // winner's own TLS handshake) finishes. A lingering loser can otherwise
        // occupy the receiver's serial AcceptAsync handshake slot for up to its
        // 5s timeout, for a connection that will never present a valid token.
        var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var attempts = new List<Task<TcpClient?>>();
        foreach (var address in ad.Addresses)
        {
            if (!IPAddress.TryParse(address, out var ip))
            {
                log?.Invoke($"[LanDirect] {address}: skipped (not a valid IP literal).");
                continue;
            }

            attempts.Add(Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var client = new TcpClient(ip.AddressFamily);
                try
                {
                    await client.ConnectAsync(ip, ad.Port, raceCts.Token);
                    log?.Invoke($"[LanDirect] {address}: connected in {sw.ElapsedMilliseconds}ms.");
                    return client;
                }
                catch (OperationCanceledException)
                {
                    log?.Invoke($"[LanDirect] {address}: timed out / cancelled after {sw.ElapsedMilliseconds}ms.");
                    client.Dispose();
                    return (TcpClient?)null;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[LanDirect] {address}: failed after {sw.ElapsedMilliseconds}ms — {ex.GetType().Name}: {ex.Message}");
                    client.Dispose();
                    return (TcpClient?)null;
                }
            }, raceCts.Token));
        }

        TcpClient? winner = null;
        try
        {
            while (attempts.Count > 0)
            {
                var finished = await Task.WhenAny(attempts);
                attempts.Remove(finished);
                var client = await finished;
                if (client is not null && winner is null)
                {
                    winner = client;
                    raceCts.Cancel(); // stop remaining attempts now, not at the end of the whole call
                }
                else
                {
                    client?.Dispose();
                }

                if (winner is not null)
                    break;
            }
        }
        finally
        {
            // Dispose raceCts only once every still-in-flight attempt (cancelled
            // above) has actually completed — disposing while a lambda is
            // mid-flight reading raceCts.Token would risk ObjectDisposedException
            // there. `attempts` here holds exactly the not-yet-finished losers,
            // since finished attempts were removed from it in the loop above.
            _ = Task.WhenAll(attempts).ContinueWith(_ => raceCts.Dispose(), TaskScheduler.Default);
        }

        return winner;
    }
}

/// <summary>
/// Receiver-side LAN listener: an ephemeral TLS server on a random port with a
/// one-time auth token. <see cref="Advertisement"/> is relayed to the sender via
/// signaling; <see cref="AcceptAsync"/> completes when the sender connects and
/// authenticates.
/// </summary>
public sealed class LanDirectListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly byte[] _token;

    /// <summary>The advertisement describing this listener, to relay to the sender.</summary>
    public LanEndpointAdvertisement Advertisement { get; }

    private LanDirectListener(TcpListener listener, X509Certificate2 certificate, byte[] token,
        LanEndpointAdvertisement advertisement)
    {
        _listener = listener;
        _certificate = certificate;
        _token = token;
        Advertisement = advertisement;
    }

    /// <summary>
    /// Starts listening on an OS-assigned port with a fresh ephemeral
    /// certificate and token. <paramref name="addresses"/> defaults to
    /// <see cref="LanDirect.GatherLanAddresses"/>; tests pass loopback.
    /// </summary>
    /// <param name="log">Optional diagnostic sink — see <see cref="LanDirect.TryConnectAsync"/>.</param>
    public static LanDirectListener Start(IReadOnlyList<IPAddress>? addresses = null, Action<string>? log = null)
    {
        var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = true; // one socket for IPv4 + IPv6
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var certificate = CreateEphemeralCertificate();
        var token = RandomNumberGenerator.GetBytes(32);
        var advertisedAddresses = (addresses ?? LanDirect.GatherLanAddresses()).Select(a => a.ToString()).ToList();

        log?.Invoke($"[LanDirect] Listening on port {port}, advertising {advertisedAddresses.Count} address(es): " +
            $"{string.Join(", ", advertisedAddresses)}");

        var ad = new LanEndpointAdvertisement(
            advertisedAddresses,
            port,
            Convert.ToBase64String(token),
            Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())));

        return new LanDirectListener(listener, certificate, token, ad);
    }

    /// <summary>
    /// Accepts connections until one completes the TLS handshake and presents
    /// the correct token, then returns the authenticated channel. Unauthorized
    /// or broken connections are dropped and listening continues.
    /// </summary>
    /// <param name="log">Optional diagnostic sink — see <see cref="LanDirect.TryConnectAsync"/>.</param>
    public async Task<TcpTransferChannel> AcceptAsync(CancellationToken ct = default, Action<string>? log = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var client = await _listener.AcceptTcpClientAsync(ct);
            var remoteEndpoint = client.Client.RemoteEndPoint;
            log?.Invoke($"[LanDirect] Accepted TCP connection from {remoteEndpoint}.");

            try
            {
                client.NoDelay = true;
                var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                // Bound the handshake+auth so a stalled connection can't block
                // the accept loop forever.
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));

                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate
                }, handshakeCts.Token);

                var presented = new byte[_token.Length];
                await ssl.ReadExactlyAsync(presented, handshakeCts.Token);

                if (!CryptographicOperations.FixedTimeEquals(presented, _token))
                {
                    log?.Invoke($"[LanDirect] Connection from {remoteEndpoint} presented an invalid auth token — dropped.");
                    client.Dispose();
                    continue;
                }

                log?.Invoke($"[LanDirect] Connection from {remoteEndpoint} authenticated.");
                return new TcpTransferChannel(client, ssl);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                client.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                // TLS/auth failure from this client — drop it, keep listening.
                log?.Invoke($"[LanDirect] Connection from {remoteEndpoint} failed handshake/auth: {ex.GetType().Name}: {ex.Message}");
                client.Dispose();
            }
        }
    }

    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=edzio-lan", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        // Export/re-import so the private key is usable by SChannel on Windows
        // (in-memory ephemeral keys are rejected by AuthenticateAsServerAsync).
        return new X509Certificate2(cert.Export(X509ContentType.Pkcs12), (string?)null,
            X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        _listener.Stop();
        _certificate.Dispose();
    }
}
