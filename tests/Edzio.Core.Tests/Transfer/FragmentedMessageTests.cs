using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Edzio.Core.Models;
using Edzio.Core.Persistence;
using Edzio.Core.Transfer;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

/// <summary>
/// A full-duplex in-memory <see cref="ITransferChannel"/> pair (backed by
/// <see cref="Channel{T}"/>) that records every message it sends — lets a test
/// run the real <see cref="TransferSession.SendAsync"/>/<see cref="TransferSession.ReceiveAsync"/>
/// concurrently while inspecting the exact wire messages exchanged.
/// </summary>
file sealed class RecordingChannel : ITransferChannel
{
    private readonly ChannelWriter<byte[]> _outbound;
    private readonly ChannelReader<byte[]> _inbound;

    public List<byte[]> SentMessages { get; } = new();

    private RecordingChannel(ChannelWriter<byte[]> outbound, ChannelReader<byte[]> inbound)
    {
        _outbound = outbound;
        _inbound = inbound;
    }

    public Task WaitForOpenAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        lock (SentMessages) SentMessages.Add(data);
        await _outbound.WriteAsync(data, ct);
    }

    public Task<byte[]> ReceiveAsync(CancellationToken ct = default) => _inbound.ReadAsync(ct).AsTask();

    public ValueTask DisposeAsync()
    {
        _outbound.TryComplete();
        return ValueTask.CompletedTask;
    }

    public static (RecordingChannel Sender, RecordingChannel Receiver) CreatePair()
    {
        var senderToReceiver = Channel.CreateUnbounded<byte[]>();
        var receiverToSender = Channel.CreateUnbounded<byte[]>();
        var sender = new RecordingChannel(senderToReceiver.Writer, receiverToSender.Reader);
        var receiver = new RecordingChannel(receiverToSender.Writer, senderToReceiver.Reader);
        return (sender, receiver);
    }
}

/// <summary>
/// Regression coverage for the manifest-size-exceeds-transport-limit bug: a
/// manifest (or resume list) whose JSON exceeds a single message's size limit
/// (262,144 bytes on WebRTC data channels) must be transparently fragmented
/// and reassembled — see docs/debug and TransferMessageType.ManifestChunk/ResumeChunk.
/// </summary>
public sealed class FragmentedMessageTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), $"edzio_fragment_test_{Guid.NewGuid():N}");

    public FragmentedMessageTests() => Directory.CreateDirectory(_testDir);
    public void Dispose() => Directory.Delete(_testDir, recursive: true);

    /// <summary>
    /// Builds a manifest with one real chunk (backed by an actual small file)
    /// plus <paramref name="totalChunks"/> - 1 synthetic placeholder chunks, and
    /// writes the real chunk's content to disk. The placeholders never need
    /// real file bytes on disk because the test pre-marks them as already
    /// received on the receiver side, so the sender's ReadChunksAsync loop
    /// skips them (reading past EOF returns 0 bytes harmlessly — see
    /// ChunkEngine.ReadChunksAsync). This forces a large manifest (and a large
    /// resume list) without needing a multi-gigabyte real file on disk.
    /// </summary>
    private async Task<(string SourceRoot, string OutputRoot, TransferManifest Manifest, byte[] RealContent)>
        BuildLargeManifestFixtureAsync(int totalChunks)
    {
        var sourceRoot = Path.Combine(_testDir, "src");
        var outputRoot = Path.Combine(_testDir, "out");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        var realContent = new byte[100];
        new Random(1).NextBytes(realContent);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "file.bin"), realContent);
        var realHash = Convert.ToHexString(SHA256.HashData(realContent)).ToLowerInvariant();

        var chunks = new List<ChunkInfo> { new(0, realContent.Length, realHash) };
        for (int i = 1; i < totalChunks; i++)
            chunks.Add(new ChunkInfo(i, 0, $"{i:X64}"[..64]));

        var fileEntry = new FileEntry("file.bin", realContent.Length, chunks);
        var manifest = new TransferManifest(Guid.NewGuid().ToString("N"), realContent.Length, new[] { fileEntry });

        return (sourceRoot, outputRoot, manifest, realContent);
    }

    [Fact(Timeout = 30000)]
    public async Task LargeManifestAndResume_FragmentAndTransferSuccessfully()
    {
        // 20,000 chunks: manifest JSON (~110 bytes/chunk) and resume JSON
        // (~30 bytes/entry for the 19,999 pre-received placeholders) both
        // comfortably exceed 262,144 bytes, so both messages require fragmentation.
        const int totalChunks = 20_000;
        var (sourceRoot, outputRoot, manifest, realContent) = await BuildLargeManifestFixtureAsync(totalChunks);

        var manifestJsonSize = JsonSerializer.SerializeToUtf8Bytes(manifest).Length;
        manifestJsonSize.Should().BeGreaterThan(262_144,
            "the test fixture must actually exceed the single-message limit, or it isn't testing fragmentation");

        var senderRepo = RepositoryFactory.Create();
        var receiverRepo = RepositoryFactory.Create();

        // Pre-mark all placeholder chunks (1..totalChunks-1) as already received
        // on the receiver, so the sender skips them without needing real data.
        await receiverRepo.SaveSessionAsync(manifest.SessionId, "PeerA", TransferDirection.Receive,
            JsonSerializer.Serialize(manifest), TransferStatus.InProgress);
        await receiverRepo.MarkChunksReceivedAsync(manifest.SessionId,
            Enumerable.Range(1, totalChunks - 1).Select(i => (0, i)).ToList());

        var (senderChannel, receiverChannel) = RecordingChannel.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var sendTask = TransferSession.SendAsync(sourceRoot, manifest, senderChannel, senderRepo, ct: cts.Token);
        var receiveTask = TransferSession.ReceiveAsync(outputRoot, "PeerA", receiverChannel, receiverRepo, ct: cts.Token);

        await Task.WhenAll(sendTask, receiveTask);

        // Every message on either side of the wire must respect the transport cap.
        senderChannel.SentMessages.Should().OnlyContain(m => m.Length <= 262_144);
        receiverChannel.SentMessages.Should().OnlyContain(m => m.Length <= 262_144);

        // The manifest and resume list actually required fragmentation (not a
        // vacuously-true test).
        senderChannel.SentMessages.Count(m => m[0] == (byte)TransferMessageType.ManifestChunk)
            .Should().BeGreaterThan(1, "the manifest must have been split across multiple messages");
        receiverChannel.SentMessages.Count(m => m[0] == (byte)TransferMessageType.ResumeChunk)
            .Should().BeGreaterThan(1, "the resume list must have been split across multiple messages");

        // All chunks (the one real send + the pre-marked placeholders) are recorded.
        (await receiverRepo.GetReceivedChunksAsync(manifest.SessionId)).Should().HaveCount(totalChunks);

        // The one real chunk's content made it to disk correctly.
        var finalPath = Path.Combine(outputRoot, "file.bin");
        var finalBytes = await File.ReadAllBytesAsync(finalPath);
        finalBytes.Should().Equal(realContent);
    }

    [Fact(Timeout = 30000)]
    public async Task SmallManifest_SentAsSingleFragment()
    {
        // Regression guard for the single-part (common) case: a small manifest
        // must still round-trip as exactly one ManifestChunk/ResumeChunk message.
        var (sourceRoot, outputRoot, manifest, realContent) = await BuildLargeManifestFixtureAsync(totalChunks: 1);

        var senderRepo = RepositoryFactory.Create();
        var receiverRepo = RepositoryFactory.Create();
        var (senderChannel, receiverChannel) = RecordingChannel.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var sendTask = TransferSession.SendAsync(sourceRoot, manifest, senderChannel, senderRepo, ct: cts.Token);
        var receiveTask = TransferSession.ReceiveAsync(outputRoot, "PeerA", receiverChannel, receiverRepo, ct: cts.Token);
        var returnedManifest = await receiveTask;
        await sendTask;

        senderChannel.SentMessages.Count(m => m[0] == (byte)TransferMessageType.ManifestChunk).Should().Be(1);
        receiverChannel.SentMessages.Count(m => m[0] == (byte)TransferMessageType.ResumeChunk).Should().Be(1);
        returnedManifest.SessionId.Should().Be(manifest.SessionId);

        var finalBytes = await File.ReadAllBytesAsync(Path.Combine(outputRoot, "file.bin"));
        finalBytes.Should().Equal(realContent);
    }
}
