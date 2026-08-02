using Edzio.Core.Transfer;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

public class ChunkEngineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ChunkEngineTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task BuildFileEntry_SingleChunkFile_ReturnsOneChunk()
    {
        var path = Path.Combine(_tempDir, "small.bin");
        await File.WriteAllBytesAsync(path, new byte[1000]);

        var entry = await ChunkEngine.BuildFileEntryAsync(path, "small.bin");

        entry.Chunks.Should().HaveCount(1);
        entry.Chunks[0].Index.Should().Be(0);
        entry.Chunks[0].SizeBytes.Should().Be(1000);
        entry.SizeBytes.Should().Be(1000);
    }

    [Fact]
    public async Task BuildFileEntry_MultiChunkFile_ReturnsCorrectChunkCount()
    {
        var path = Path.Combine(_tempDir, "big.bin");
        var size = ChunkEngine.ChunkSize * 3 + 100; // 3 full + 1 partial
        await File.WriteAllBytesAsync(path, new byte[size]);

        var entry = await ChunkEngine.BuildFileEntryAsync(path, "big.bin");

        entry.Chunks.Should().HaveCount(4);
        entry.Chunks[3].SizeBytes.Should().Be(100);
    }

    [Fact]
    public async Task BuildFileEntry_ChunkHasSha256()
    {
        var path = Path.Combine(_tempDir, "hash.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

        var entry = await ChunkEngine.BuildFileEntryAsync(path, "hash.bin");

        entry.Chunks[0].Sha256.Should().HaveLength(64); // 32 bytes hex
    }

    [Fact]
    public async Task ReadChunksAsync_SkipsAlreadyReceivedChunks()
    {
        var path = Path.Combine(_tempDir, "resume.bin");
        var size = ChunkEngine.ChunkSize * 2;
        await File.WriteAllBytesAsync(path, new byte[size]);

        var entry = await ChunkEngine.BuildFileEntryAsync(path, "resume.bin");
        var manifest = new Core.Models.TransferManifest("s1", size, new[] { entry });

        var skip = new HashSet<(int, int)> { (0, 0) }; // skip first chunk
        var chunks = new List<(int FileIndex, int ChunkIndex, byte[] Data)>();
        await foreach (var chunk in ChunkEngine.ReadChunksAsync(_tempDir, manifest, skip))
            chunks.Add(chunk);

        chunks.Should().HaveCount(1);
        chunks[0].ChunkIndex.Should().Be(1);
    }

    [Fact]
    public void ChunkSize_PlusHeader_DoesNotExceedSctpMaxMessageSize()
    {
        // SIPSorcery's RTCSctpTransport.SCTP_DEFAULT_MAX_MESSAGE_SIZE is a fixed,
        // non-configurable 262144-byte cap on RTCDataChannel.send(). A full-size
        // Chunk message (header + data) must stay within that limit, or sends fail
        // with "exceeded the maximum allowed message size" (see chunk-size-exceeds-sctp-limit
        // debug investigation).
        const int sctpMaxMessageSize = 262144;

        var fullChunkMessageSize = TransferSession.ChunkHeaderSize + ChunkEngine.ChunkSize;

        fullChunkMessageSize.Should().BeLessThanOrEqualTo(sctpMaxMessageSize);
    }

    [Fact]
    public async Task WriteChunkAsync_ThenFinalize_ProducesOriginalFile()
    {
        // Multi-chunk so offset math ((long)chunkIndex * ChunkSize) is exercised.
        var original = new byte[ChunkEngine.ChunkSize + 100];
        new Random(42).NextBytes(original);
        var srcPath = Path.Combine(_tempDir, "orig.bin");
        await File.WriteAllBytesAsync(srcPath, original);

        var entry = await ChunkEngine.BuildFileEntryAsync(srcPath, "orig.bin");
        var manifest = new Core.Models.TransferManifest("s1", original.Length, new[] { entry });

        var outDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outDir);

        await using (var partStream = ChunkEngine.OpenPartStream(outDir, manifest, 0))
        {
            await foreach (var (fi, ci, data) in ChunkEngine.ReadChunksAsync(_tempDir, manifest, new HashSet<(int,int)>()))
                await ChunkEngine.WriteChunkAsync(partStream, manifest, fi, ci, data);
        }

        ChunkEngine.FinalizeFile(outDir, manifest, 0);

        var finalPath = Path.Combine(outDir, "orig.bin");
        File.Exists(finalPath + ".part").Should().BeFalse();
        var result = await File.ReadAllBytesAsync(finalPath);
        result.Should().Equal(original);
    }

    [Fact]
    public async Task OpenPartStream_StaleLargerPartFileOnDisk_TruncatesToExpectedSize()
    {
        // Regression test for a PR review finding: FileMode.OpenOrCreate reuses an
        // existing .part file as-is. If a stale .part from an earlier, larger/
        // different transfer occupies the same relative path, its trailing bytes
        // beyond this manifest's expected size must not survive into the final file.
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var srcPath = Path.Combine(_tempDir, "orig.bin");
        await File.WriteAllBytesAsync(srcPath, content);

        var entry = await ChunkEngine.BuildFileEntryAsync(srcPath, "orig.bin");
        var manifest = new Core.Models.TransferManifest("s1", content.Length, new[] { entry });

        var outDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outDir);

        // Simulate a stale .part file much larger than this manifest's expected size.
        var stalePartPath = Path.Combine(outDir, "orig.bin.part");
        await File.WriteAllBytesAsync(stalePartPath, new byte[content.Length + 1000]);

        await using (var partStream = ChunkEngine.OpenPartStream(outDir, manifest, 0))
        {
            partStream.Length.Should().Be(content.Length,
                "the stale trailing bytes from a previous, larger transfer must be truncated immediately on open");

            await ChunkEngine.WriteChunkAsync(partStream, manifest, 0, 0, content);
        }

        ChunkEngine.FinalizeFile(outDir, manifest, 0);

        var result = await File.ReadAllBytesAsync(Path.Combine(outDir, "orig.bin"));
        result.Should().Equal(content, "the final file must not contain any stale trailing bytes");
    }

    [Fact]
    public async Task WriteChunkAsync_HashMismatch_Throws()
    {
        var original = new byte[] { 10, 20, 30 };
        var srcPath = Path.Combine(_tempDir, "orig.bin");
        await File.WriteAllBytesAsync(srcPath, original);

        var entry = await ChunkEngine.BuildFileEntryAsync(srcPath, "orig.bin");
        var manifest = new Core.Models.TransferManifest("s1", original.Length, new[] { entry });

        var outDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outDir);

        await using var partStream = ChunkEngine.OpenPartStream(outDir, manifest, 0);
        var corrupted = new byte[] { 99, 99, 99 };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ChunkEngine.WriteChunkAsync(partStream, manifest, 0, 0, corrupted));
    }
}
