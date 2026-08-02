using System.Text;
using System.Text.Json;
using Edzio.Core.Models;
using Edzio.Core.Persistence;
using Edzio.Core.Transfer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

// ---------------------------------------------------------------------------
// Synchronous IProgress<T> to avoid thread-pool timing issues
// ---------------------------------------------------------------------------
internal sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

// ---------------------------------------------------------------------------
// Stub channel — returns a pre-queued sequence of messages from the sender.
// ---------------------------------------------------------------------------
internal sealed class StubChannel : ITransferChannel
{
    private readonly Queue<byte[]> _inbound = new();
    private readonly List<byte[]>  _sent    = new();

    public IReadOnlyList<byte[]> SentMessages => _sent;

    public void EnqueueInbound(byte[] message) => _inbound.Enqueue(message);

    public Task WaitForOpenAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SendAsync(byte[] data, CancellationToken ct = default) { _sent.Add(data); return Task.CompletedTask; }
    public Task<byte[]> ReceiveAsync(CancellationToken ct = default)
        => _inbound.Count > 0
            ? Task.FromResult(_inbound.Dequeue())
            : Task.FromException<byte[]>(new InvalidOperationException("No more inbound messages."));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
internal static class MessageBuilder
{
    /// <summary>Builds a single-fragment Manifest message: [0x06][totalParts=1][partIndex=0][JSON].</summary>
    public static byte[] ManifestMessage(TransferManifest manifest)
    {
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));
        byte[] msg  = new byte[9 + json.Length];
        msg[0] = (byte)TransferMessageType.ManifestChunk;
        WriteInt32LE(msg, 1, 1); // totalParts
        WriteInt32LE(msg, 5, 0); // partIndex
        json.CopyTo(msg, 9);
        return msg;
    }

    private static void WriteInt32LE(byte[] buf, int offset, int value)
    {
        buf[offset + 0] = (byte)(value);
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    public static byte[] ChunkMessage(int fileIndex, int chunkIndex, byte[] data)
    {
        byte[] msg = new byte[9 + data.Length];
        msg[0] = (byte)TransferMessageType.Chunk;
        BitConverter.GetBytes(fileIndex).CopyTo(msg, 1);
        BitConverter.GetBytes(chunkIndex).CopyTo(msg, 5);
        data.CopyTo(msg, 9);
        return msg;
    }

    public static byte[] DoneMessage()       => new[] { (byte)TransferMessageType.Done };
    public static byte[] ErrorMessage(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        byte[] msg     = new byte[1 + payload.Length];
        msg[0] = (byte)TransferMessageType.Error;
        payload.CopyTo(msg, 1);
        return msg;
    }
}

internal static class RepositoryFactory
{
    // Each call returns an in-memory SQLite-backed repository with a unique DB.
    public static TransferRepository Create()
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

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------
public sealed class TransferSessionReceiveTests : IDisposable
{
    private readonly string _outputRoot;

    public TransferSessionReceiveTests()
    {
        _outputRoot = Path.Combine(Path.GetTempPath(), $"edzio_recv_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outputRoot);
    }

    public void Dispose() => Directory.Delete(_outputRoot, recursive: true);

    // Build a simple single-file, two-chunk manifest for testing.
    private static (TransferManifest Manifest, byte[] Chunk0Data, byte[] Chunk1Data) BuildTwoChunkFixture()
    {
        byte[] chunk0 = Encoding.UTF8.GetBytes("Hello, ");
        byte[] chunk1 = Encoding.UTF8.GetBytes("World!");

        var manifest = new TransferManifest(
            SessionId:  Guid.NewGuid().ToString("N"),
            TotalBytes: chunk0.Length + chunk1.Length,
            Files: new[]
            {
                new FileEntry(
                    RelativePath: "greeting.txt",
                    SizeBytes:    chunk0.Length + chunk1.Length,
                    Chunks: new[]
                    {
                        new ChunkInfo(0, chunk0.Length, ComputeSha256(chunk0)),
                        new ChunkInfo(1, chunk1.Length, ComputeSha256(chunk1)),
                    })
            });

        return (manifest, chunk0, chunk1);
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // Test 1 — Fresh receive: two chunks arrive, file assembled on disk.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FreshReceive_AssemblesFileOnDisk()
    {
        var (manifest, c0, c1) = BuildTwoChunkFixture();
        var repo    = RepositoryFactory.Create();
        var channel = new StubChannel();

        channel.EnqueueInbound(MessageBuilder.ManifestMessage(manifest));
        channel.EnqueueInbound(MessageBuilder.ChunkMessage(0, 0, c0));
        channel.EnqueueInbound(MessageBuilder.ChunkMessage(0, 1, c1));
        channel.EnqueueInbound(MessageBuilder.DoneMessage());

        var returned = await TransferSession.ReceiveAsync(
            _outputRoot, "PeerA", channel, repo);

        Assert.Equal(manifest.SessionId, returned.SessionId);

        string assembled = Path.Combine(_outputRoot, "greeting.txt");
        Assert.True(File.Exists(assembled));
        Assert.Equal("Hello, World!", await File.ReadAllTextAsync(assembled));
    }

    // ------------------------------------------------------------------
    // Test 2 — Fresh receive: all chunks marked in DB.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FreshReceive_MarksAllChunksInRepository()
    {
        var (manifest, c0, c1) = BuildTwoChunkFixture();
        var repo    = RepositoryFactory.Create();
        var channel = new StubChannel();

        channel.EnqueueInbound(MessageBuilder.ManifestMessage(manifest));
        channel.EnqueueInbound(MessageBuilder.ChunkMessage(0, 0, c0));
        channel.EnqueueInbound(MessageBuilder.ChunkMessage(0, 1, c1));
        channel.EnqueueInbound(MessageBuilder.DoneMessage());

        await TransferSession.ReceiveAsync(_outputRoot, "PeerA", channel, repo);

        var received = await repo.GetReceivedChunksAsync(manifest.SessionId);
        Assert.Equal(2, received.Count);
        Assert.Contains((0, 0), received);
        Assert.Contains((0, 1), received);
    }

    // ------------------------------------------------------------------
    // Test 3 — Resume: pre-populate chunk (0,0); Resume message must list it.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ResumeReceive_SendsResumeListingExistingChunk()
    {
        var (manifest, c0, c1) = BuildTwoChunkFixture();
        var repo    = RepositoryFactory.Create();

        // Pre-populate: session exists with chunk (0,0) already received
        await repo.SaveSessionAsync(
            manifest.SessionId, "PeerA",
            TransferDirection.Receive,
            JsonSerializer.Serialize(manifest),
            TransferStatus.InProgress);
        await repo.MarkChunkReceivedAsync(manifest.SessionId, 0, 0);

        var channel = new StubChannel();
        channel.EnqueueInbound(MessageBuilder.ManifestMessage(manifest));
        // Sender still sends both chunks (sender decides what to skip based on Resume)
        channel.EnqueueInbound(MessageBuilder.ChunkMessage(0, 0, c0));
        channel.EnqueueInbound(MessageBuilder.ChunkMessage(0, 1, c1));
        channel.EnqueueInbound(MessageBuilder.DoneMessage());

        await TransferSession.ReceiveAsync(_outputRoot, "PeerA", channel, repo);

        // The first sent message (index 0 = ResumeChunk) should list fileIndex=0, chunkIndex=0
        Assert.True(channel.SentMessages.Count >= 1);
        byte[] resumeMsg = channel.SentMessages[0];
        Assert.Equal((byte)TransferMessageType.ResumeChunk, resumeMsg[0]);

        string resumeJson = Encoding.UTF8.GetString(resumeMsg, 9, resumeMsg.Length - 9);
        using var doc = JsonDocument.Parse(resumeJson);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(0, items[0].GetProperty("fileIndex").GetInt32());
        Assert.Equal(0, items[0].GetProperty("chunkIndex").GetInt32());
    }

    // ------------------------------------------------------------------
    // Test 4 — Error from sender raises TransferException.
    // ------------------------------------------------------------------
    [Fact]
    public async Task SenderError_ThrowsTransferException()
    {
        var (manifest, _, _) = BuildTwoChunkFixture();
        var repo    = RepositoryFactory.Create();
        var channel = new StubChannel();

        channel.EnqueueInbound(MessageBuilder.ManifestMessage(manifest));
        channel.EnqueueInbound(MessageBuilder.ErrorMessage("Disk full on sender"));

        var ex = await Assert.ThrowsAsync<TransferException>(
            () => TransferSession.ReceiveAsync(_outputRoot, "PeerA", channel, repo));

        Assert.Contains("Disk full on sender", ex.Message);
    }

    // ------------------------------------------------------------------
    // Test 5 — Progress reported for each chunk with correct BytesSent.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Progress_ReportedPerChunkWithCorrectBytesSent()
    {
        var (manifest, c0, c1) = BuildTwoChunkFixture();
        var repo    = RepositoryFactory.Create();
        var channel = new StubChannel();

        channel.EnqueueInbound(MessageBuilder.ManifestMessage(manifest));
        channel.EnqueueInbound(MessageBuilder.ChunkMessage(0, 0, c0));
        channel.EnqueueInbound(MessageBuilder.ChunkMessage(0, 1, c1));
        channel.EnqueueInbound(MessageBuilder.DoneMessage());

        var reports = new List<TransferProgress>();
        var progressSpy = new SyncProgress<TransferProgress>(p => reports.Add(p));

        await TransferSession.ReceiveAsync(_outputRoot, "PeerA", channel, repo, progressSpy);

        Assert.Equal(2, reports.Count);
        Assert.Equal(c0.Length,              reports[0].BytesSent);
        Assert.Equal(c0.Length + c1.Length,  reports[1].BytesSent);
        Assert.Equal(manifest.TotalBytes,    reports[1].TotalBytes);
        Assert.Equal(1, reports[0].ChunksComplete);
        Assert.Equal(2, reports[1].ChunksComplete);
        Assert.Equal(2, reports[1].ChunksTotal);
    }
}
