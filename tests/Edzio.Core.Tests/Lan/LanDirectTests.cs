using System.Net;
using Edzio.Core.Lan;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Lan;

public class LanDirectTests
{
    private static readonly IReadOnlyList<IPAddress> Loopback = new[] { IPAddress.Loopback };

    [Fact(Timeout = 20000)]
    public async Task ListenerAndConnect_Roundtrip_MessagesSurviveFraming()
    {
        var logLines = new List<string>();
        using var listener = LanDirectListener.Start(Loopback, log: logLines.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var acceptTask = listener.AcceptAsync(cts.Token, log: logLines.Add);
        var senderChannel = await LanDirect.TryConnectAsync(
            listener.Advertisement, TimeSpan.FromSeconds(5), log: logLines.Add, ct: cts.Token);

        senderChannel.Should().NotBeNull();
        await using var sender = senderChannel!;
        await using var receiver = await acceptTask;

        // Small message + chunk-sized message, both directions. The reader
        // must run concurrently with big sends: a 256 KB message exceeds the
        // loopback TCP buffers, so a write-then-read sequence would deadlock
        // (production always reads concurrently — see TransferSession).
        var small = new byte[] { 1, 2, 3 };
        var big = new byte[262_144];
        new Random(7).NextBytes(big);

        await sender.SendAsync(small, cts.Token);
        (await receiver.ReceiveAsync(cts.Token)).Should().Equal(small);

        var receiveBig = receiver.ReceiveAsync(cts.Token);
        await sender.SendAsync(big, cts.Token);
        (await receiveBig).Should().Equal(big);

        var receiveBigBack = sender.ReceiveAsync(cts.Token);
        await receiver.SendAsync(big, cts.Token);
        (await receiveBigBack).Should().Equal(big);

        // Diagnostics (added after intermittent LAN-direct fallback was observed
        // in production with no visibility into why) must report the successful
        // per-address connect and the receiver-side accept/authenticate outcome.
        logLines.Should().Contain(l => l.Contains("connected in") && l.Contains("ms"));
        logLines.Should().Contain(l => l.Contains("Accepted TCP connection from"));
        logLines.Should().Contain(l => l.Contains("authenticated"));
    }

    [Fact(Timeout = 20000)]
    public async Task TryConnect_WrongCertFingerprint_ReturnsNull()
    {
        using var listener = LanDirectListener.Start(Loopback);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Keep the listener's accept loop alive so the TCP connect itself succeeds
        // and only the TLS validation can be the reason for failure.
        var acceptTask = listener.AcceptAsync(cts.Token);

        var logLines = new List<string>();
        var tampered = listener.Advertisement with { CertSha256Hex = new string('0', 64) };
        var channel = await LanDirect.TryConnectAsync(tampered, TimeSpan.FromSeconds(5), log: logLines.Add, ct: cts.Token);

        channel.Should().BeNull();
        logLines.Should().Contain(l => l.Contains("TLS handshake/auth") && l.Contains("failed"));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acceptTask);
    }

    [Fact(Timeout = 20000)]
    public async Task TryConnect_NothingListening_ReturnsNullWithinTimeout()
    {
        // A port with no listener: grab one, then release it.
        int freePort;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var ad = new LanEndpointAdvertisement(
            new[] { IPAddress.Loopback.ToString() }, freePort,
            Convert.ToBase64String(new byte[32]), new string('0', 64));

        var channel = await LanDirect.TryConnectAsync(ad, TimeSpan.FromSeconds(2));
        channel.Should().BeNull();
    }

    [Fact]
    public void Advertisement_SerializeThenParse_Roundtrips()
    {
        var ad = new LanEndpointAdvertisement(
            new[] { "192.168.1.13", "2a01:112f::1" }, 54321,
            Convert.ToBase64String(new byte[32]), new string('a', 64));

        var json = LanDirect.SerializeAdvertisement(ad);
        var parsed = LanDirect.TryParseAdvertisement(json);

        parsed.Should().Be(ad with { Addresses = parsed!.Addresses }); // record list equality is by reference
        parsed.Addresses.Should().Equal(ad.Addresses);
        parsed.Port.Should().Be(ad.Port);
    }

    [Theory]
    [InlineData("""{"candidate":"1234 1 udp ...","sdpMid":"0"}""")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void TryParseAdvertisement_NonAdvertisementInput_ReturnsNull(string input)
    {
        LanDirect.TryParseAdvertisement(input).Should().BeNull();
    }

    [Fact(Timeout = 60000)]
    public async Task TransferSession_OverLanDirectChannel_TransfersRealFile()
    {
        // End-to-end: the real send/receive pipeline over the LAN channel with a
        // production-sized payload (matches the 18.5 MB real-world test case).
        var testDir = Path.Combine(Path.GetTempPath(), $"edzio_lan_e2e_{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testDir, "src");
        var outputRoot = Path.Combine(testDir, "out");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        try
        {
            var data = new byte[18_500_000];
            new Random(7).NextBytes(data);
            var sourceFile = Path.Combine(sourceRoot, "payload.bin");
            await File.WriteAllBytesAsync(sourceFile, data);

            var entry = await Core.Transfer.ChunkEngine.BuildFileEntryAsync(sourceFile, "payload.bin");
            var manifest = new Core.Models.TransferManifest(
                Guid.NewGuid().ToString("N"), data.Length, new[] { entry });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            using var listener = LanDirectListener.Start(Loopback);
            var acceptTask = listener.AcceptAsync(cts.Token);
            await using var senderChannel = (await LanDirect.TryConnectAsync(
                listener.Advertisement, TimeSpan.FromSeconds(5), ct: cts.Token))!;
            await using var receiverChannel = await acceptTask;

            var sendTask = Core.Transfer.TransferSession.SendAsync(
                sourceRoot, manifest, senderChannel,
                Core.Tests.Transfer.RepositoryFactory.Create(), ct: cts.Token);
            var receiveTask = Core.Transfer.TransferSession.ReceiveAsync(
                outputRoot, "PeerA", receiverChannel,
                Core.Tests.Transfer.RepositoryFactory.Create(), ct: cts.Token);

            await Task.WhenAll(sendTask, receiveTask);

            var result = await File.ReadAllBytesAsync(Path.Combine(outputRoot, "payload.bin"));
            result.AsSpan().SequenceEqual(data).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }
}
