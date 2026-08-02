using Edzio.Core.Persistence;
using Edzio.Core.Tests.Signaling;
using Edzio.Core.Tests.WebRtc;
using Edzio.Core.WebRtc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SIPSorcery.Net;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

/// <summary>
/// Diagnostic benchmark (not a correctness test) for the slow-transfer
/// investigation (docs/debug/slow-webrtc-transfer-throughput). Runs the real
/// <see cref="TransferSession.SendAsync"/>/<see cref="TransferSession.ReceiveAsync"/>
/// pipeline end-to-end over a real (loopback) <see cref="WebRtcChannel"/> pair, with
/// a real on-disk SQLite database on both sides (matching production — not the
/// ":memory:" DB used by the other TransferSession tests, since fsync/commit cost
/// only shows up with a real file) and a real temp output directory. Isolates
/// whether the receiver's per-chunk disk + SQLite work is what caps observed
/// real-world throughput at ~1 MB/s despite the transport itself sustaining
/// ~13 MB/s on loopback (see WebRtcChannelLoopbackTest.Benchmark_RawChannelThroughput_18MB).
/// </summary>
public sealed class FullPipelineBenchmarkTests : IDisposable
{
    private readonly string _testDir =
        Path.Combine(Path.GetTempPath(), $"edzio_pipeline_bench_{Guid.NewGuid():N}");

    private readonly List<IDisposable> _disposables = new();

    public FullPipelineBenchmarkTests() => Directory.CreateDirectory(_testDir);

    public void Dispose()
    {
        // EF does not own externally-opened connections — dispose both contexts
        // and connections, then clear the SQLite pool so the .db file locks are
        // released before the directory delete.
        foreach (var d in _disposables)
            d.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_testDir, recursive: true);
    }

    private TransferRepository CreateOnDiskRepository(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        var opts = new DbContextOptionsBuilder<TransferDbContext>().UseSqlite(conn).Options;
        var db = new TransferDbContext(opts);
        db.Database.EnsureCreated();
        _disposables.Add(db);
        _disposables.Add(conn);
        return new TransferRepository(db);
    }

    [Fact(Timeout = 60000, Skip = "Diagnostic benchmark — intentionally 'fails' to report timing. Remove Skip to run.")]
    public async Task Benchmark_FullPipeline_18MB_RealSqliteAndDisk()
    {
        // ── Arrange: an 18.5 MB source file, on-disk SQLite DBs, real temp dirs ──
        var sourceRoot = Path.Combine(_testDir, "src");
        var outputRoot = Path.Combine(_testDir, "out");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        var sourceFile = Path.Combine(sourceRoot, "test.bin");
        var data = new byte[18_500_000];
        new Random(7).NextBytes(data);
        await File.WriteAllBytesAsync(sourceFile, data);

        var entry = await Core.Transfer.ChunkEngine.BuildFileEntryAsync(sourceFile, "test.bin");
        var manifest = new Core.Models.TransferManifest(
            Guid.NewGuid().ToString("N"), data.Length, new[] { entry });

        var senderRepo   = CreateOnDiskRepository(Path.Combine(_testDir, "sender.db"));
        var receiverRepo = CreateOnDiskRepository(Path.Combine(_testDir, "receiver.db"));

        var paired = new PairedFakeSignaling();
        var config = new RTCConfiguration(); // host candidates only — no STUN needed

        var offererChannel = new WebRtcChannel(config, paired.Offerer, WebRtcRole.Offerer);
        await using var answererChannel = new WebRtcChannel(config, paired.Answerer, WebRtcRole.Answerer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(55));

        // ICE/SDP negotiation — excluded from the timed portion, matches how a
        // real transfer's "connect" phase is separate from its "transfer" phase.
        await Task.WhenAll(
            answererChannel.ConnectAsync(cts.Token),
            offererChannel.ConnectAsync(cts.Token));
        await Task.WhenAll(
            offererChannel.WaitForOpenAsync(cts.Token),
            answererChannel.WaitForOpenAsync(cts.Token));

        // ── Act: run the real send/receive pipeline, timed ──
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var sendTask = Core.Transfer.TransferSession.SendAsync(
            sourceRoot, manifest, offererChannel, senderRepo, ct: cts.Token);
        var receiveTask = Core.Transfer.TransferSession.ReceiveAsync(
            outputRoot, "PeerA", answererChannel, receiverRepo, ct: cts.Token);

        await Task.WhenAll(sendTask, receiveTask);

        sw.Stop();

        await offererChannel.DisposeAsync();

        double mbPerSec = data.Length / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;

        // Correctness check, then surface the timing via a thrown exception since
        // `dotnet test` doesn't reliably show ITestOutputHelper output at normal
        // verbosity — mirrors the pattern used by the raw-channel benchmark.
        var resultFile = Path.Combine(outputRoot, "test.bin");
        var resultBytes = await File.ReadAllBytesAsync(resultFile);

        throw new Xunit.Sdk.XunitException(
            $"BENCHMARK RESULT (not a failure): {data.Length:N0} bytes, " +
            $"{sw.Elapsed.TotalSeconds:F2}s, {mbPerSec:F2} MB/s, " +
            $"assembledMatchesSource={resultBytes.AsSpan().SequenceEqual(data)}");
    }
}
