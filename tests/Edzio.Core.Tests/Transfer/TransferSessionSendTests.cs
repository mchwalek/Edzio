using System.Text.Json;
using Edzio.Core.Models;
using Edzio.Core.Persistence;
using Edzio.Core.Transfer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

// ── Helpers ──────────────────────────────────────────────────────────────────

file static class MessageHelpers
{
    public static byte[] ResumeMessage(IEnumerable<(int fi, int ci)>? skip = null)
    {
        var entries = (skip ?? Enumerable.Empty<(int, int)>())
            .Select(t => new { fileIndex = t.fi, chunkIndex = t.ci })
            .ToList();

        var json = JsonSerializer.SerializeToUtf8Bytes(entries);
        var msg  = new byte[1 + json.Length];
        msg[0]   = 0x02; // Resume
        json.CopyTo(msg, 1);
        return msg;
    }

    public static byte[] ErrorMessage() => new[] { (byte)0x05 };

    /// <summary>Decode a chunk message built by TransferSession.</summary>
    public static (byte type, int fileIndex, int chunkIndex, byte[] data) DecodeChunk(byte[] msg)
    {
        var fi = ReadInt32LE(msg, 1);
        var ci = ReadInt32LE(msg, 5);
        var d  = msg[9..];
        return (msg[0], fi, ci, d);
    }

    public static bool IsManifest(byte[] msg) => msg.Length > 0 && msg[0] == 0x01;
    public static bool IsDone(byte[]    msg) => msg.Length == 1 && msg[0] == 0x04;
    public static bool IsChunk(byte[]   msg) => msg.Length > 0 && msg[0] == 0x03;

    private static int ReadInt32LE(byte[] b, int o) =>
        b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
}

// ── Factory helpers ───────────────────────────────────────────────────────────

file static class TestData
{
    /// <summary>
    /// Creates a minimal TransferManifest with a single file containing
    /// <paramref name="chunkCount"/> chunks (no real disk content needed for protocol tests).
    /// </summary>
    public static TransferManifest SingleFileManifest(int chunkCount = 2, int chunkSize = 16)
    {
        var chunks = Enumerable.Range(0, chunkCount)
            .Select(i => new ChunkInfo(i, chunkSize, $"sha256-{i}"))
            .ToList();

        var file = new FileEntry("file.bin", (long)chunkCount * chunkSize, chunks);

        return new TransferManifest(
            SessionId:  Guid.NewGuid().ToString("N"),
            TotalBytes: file.SizeBytes,
            Files:      new[] { file });
    }

    /// <summary>Builds an in-memory SQLite TransferRepository.</summary>
    public static TransferRepository InMemoryRepository()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<TransferDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new TransferDbContext(opts);
        db.Database.EnsureCreated();
        return new TransferRepository(db);
    }
}

// ── A stub ITransferChannel that replays canned ReceiveAsync messages ─────────

file sealed class StubChannel : ITransferChannel
{
    private readonly Queue<byte[]> _receiveQueue;
    public List<byte[]> Sent { get; } = new();

    public StubChannel(params byte[][] receiveMessages)
    {
        _receiveQueue = new Queue<byte[]>(receiveMessages);
    }

    public Task WaitForOpenAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        Sent.Add(data);
        return Task.CompletedTask;
    }

    public Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        if (_receiveQueue.TryDequeue(out var msg))
            return Task.FromResult(msg);
        throw new InvalidOperationException("No more messages in StubChannel queue.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ── A fake ChunkEngine source ─────────────────────────────────────────────────
// ChunkEngine.ReadChunksAsync reads from disk; we need deterministic data in tests.
// Strategy: write real (tiny) files to a temp directory.

file static class TempFiles
{
    /// <summary>
    /// Writes a temporary file whose bytes are deterministic per chunk,
    /// returns the directory root and the real TransferManifest built from it.
    /// </summary>
    public static async Task<(string root, TransferManifest manifest)> CreateAsync(
        int chunkCount = 2)
    {
        var root     = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);

        // Each chunk is ChunkEngine.ChunkSize bytes; for tests use smaller data
        // by writing exactly chunkCount * 16 bytes (well under one chunk — single chunk result).
        // For multi-chunk tests, write ChunkEngine.ChunkSize * chunkCount bytes.
        const int chunkSize = 262144; // matches ChunkEngine.ChunkSize
        var content = new byte[chunkSize * chunkCount];
        new Random(42).NextBytes(content);

        var filePath = Path.Combine(root, "file.bin");
        await File.WriteAllBytesAsync(filePath, content);

        var manifest = await TransferManifestBuilder.BuildAsync(
            Guid.NewGuid().ToString("N"),
            new[] { filePath });

        return (root, manifest);
    }

    public static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
    }
}

// ── Synchronous IProgress<T> to avoid thread-pool timing issues ───────────────

file sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public class TransferSessionSendTests
{
    // ── 1. Fresh send: manifest → chunks in order → done ─────────────────

    [Fact]
    public async Task FreshSend_SendsManifestThenChunksThenDone()
    {
        var (root, manifest) = await TempFiles.CreateAsync(chunkCount: 2);
        try
        {
            var repo    = TestData.InMemoryRepository();
            var channel = new StubChannel(MessageHelpers.ResumeMessage()); // no skips

            await TransferSession.SendAsync(root, manifest, channel, repo);

            var sent = channel.Sent;
            // First message must be Manifest
            Assert.True(MessageHelpers.IsManifest(sent[0]),
                "First message should be Manifest (0x01)");

            // Middle messages must all be Chunk
            var chunks = sent.Skip(1).Take(sent.Count - 2).ToList();
            Assert.All(chunks, msg => Assert.True(MessageHelpers.IsChunk(msg),
                "Middle messages should be Chunk (0x03)"));

            // Chunks must be in order: (fileIndex=0, chunkIndex=0), then (0,1)
            var (_, fi0, ci0, _) = MessageHelpers.DecodeChunk(chunks[0]);
            var (_, fi1, ci1, _) = MessageHelpers.DecodeChunk(chunks[1]);
            Assert.Equal((0, 0), (fi0, ci0));
            Assert.Equal((0, 1), (fi1, ci1));

            // Last message must be Done
            Assert.True(MessageHelpers.IsDone(sent[^1]),
                "Last message should be Done (0x04)");
        }
        finally
        {
            TempFiles.Cleanup(root);
        }
    }

    // ── 2. Resume: skipped chunk is NOT sent; subsequent chunk IS sent ────

    [Fact]
    public async Task ResumeSend_SkipsRequestedChunks()
    {
        var (root, manifest) = await TempFiles.CreateAsync(chunkCount: 2);
        try
        {
            var repo = TestData.InMemoryRepository();
            // Receiver says chunk (fileIndex=0, chunkIndex=0) is already received
            var channel = new StubChannel(
                MessageHelpers.ResumeMessage(new[] { (0, 0) }));

            await TransferSession.SendAsync(root, manifest, channel, repo);

            var chunkMessages = channel.Sent
                .Where(MessageHelpers.IsChunk)
                .Select(MessageHelpers.DecodeChunk)
                .ToList();

            // Chunk (0,0) must NOT appear
            Assert.DoesNotContain(chunkMessages, c => c.fileIndex == 0 && c.chunkIndex == 0);

            // Chunk (0,1) MUST appear
            Assert.Contains(chunkMessages, c => c.fileIndex == 0 && c.chunkIndex == 1);
        }
        finally
        {
            TempFiles.Cleanup(root);
        }
    }

    // ── 3. Progress: IProgress<T> receives correct BytesSent values ───────

    [Fact]
    public async Task SendAsync_ReportsProgressAfterEachChunk()
    {
        var (root, manifest) = await TempFiles.CreateAsync(chunkCount: 2);
        try
        {
            var repo     = TestData.InMemoryRepository();
            var channel  = new StubChannel(MessageHelpers.ResumeMessage());
            var reports  = new List<TransferProgress>();
            var progress = new SyncProgress<TransferProgress>(p => reports.Add(p));

            await TransferSession.SendAsync(root, manifest, channel, repo, progress);

            // One progress report per chunk
            Assert.Equal(2, reports.Count);

            // BytesSent should increase monotonically
            Assert.True(reports[0].BytesSent > 0);
            Assert.True(reports[1].BytesSent > reports[0].BytesSent);

            // Final report BytesSent should equal TotalBytes
            Assert.Equal(manifest.TotalBytes, reports[^1].BytesSent);

            // ChunksComplete counts should be 1, then 2
            Assert.Equal(1, reports[0].ChunksComplete);
            Assert.Equal(2, reports[1].ChunksComplete);
        }
        finally
        {
            TempFiles.Cleanup(root);
        }
    }

    // ── 4. Error from receiver → throws TransferException ─────────────────

    [Fact]
    public async Task SendAsync_WhenReceiverSendsError_ThrowsTransferException()
    {
        var (root, manifest) = await TempFiles.CreateAsync(chunkCount: 1);
        try
        {
            var repo    = TestData.InMemoryRepository();
            // Receiver replies with an Error message instead of Resume
            var channel = new StubChannel(MessageHelpers.ErrorMessage());

            await Assert.ThrowsAsync<TransferException>(
                () => TransferSession.SendAsync(root, manifest, channel, repo));
        }
        finally
        {
            TempFiles.Cleanup(root);
        }
    }

    // ── 5. Session is persisted and status updated to Completed ───────────

    [Fact]
    public async Task SendAsync_PersistsSessionAndMarksCompleted()
    {
        var (root, manifest) = await TempFiles.CreateAsync(chunkCount: 1);
        try
        {
            var repo    = TestData.InMemoryRepository();
            var channel = new StubChannel(MessageHelpers.ResumeMessage());

            await TransferSession.SendAsync(root, manifest, channel, repo);

            var session = await repo.GetSessionAsync(manifest.SessionId);
            Assert.NotNull(session);
            Assert.Equal(TransferStatus.Completed, session!.Status);
            Assert.Equal(TransferDirection.Send,   session.Direction);
        }
        finally
        {
            TempFiles.Cleanup(root);
        }
    }
}
