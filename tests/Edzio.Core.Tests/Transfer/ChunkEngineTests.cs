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
    public async Task WriteChunkAsync_ThenAssemble_ProducesOriginalFile()
    {
        var original = new byte[] { 10, 20, 30, 40, 50 };
        var srcPath = Path.Combine(_tempDir, "orig.bin");
        await File.WriteAllBytesAsync(srcPath, original);

        var entry = await ChunkEngine.BuildFileEntryAsync(srcPath, "orig.bin");
        var manifest = new Core.Models.TransferManifest("s1", original.Length, new[] { entry });

        var outDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outDir);

        await foreach (var (fi, ci, data) in ChunkEngine.ReadChunksAsync(_tempDir, manifest, new HashSet<(int,int)>()))
            await ChunkEngine.WriteChunkAsync(outDir, manifest, fi, ci, data);

        await ChunkEngine.AssembleFileAsync(outDir, manifest, 0);

        var finalPath = Path.Combine(outDir, "orig.bin");
        var result = await File.ReadAllBytesAsync(finalPath);
        result.Should().Equal(original);
    }
}
