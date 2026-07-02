using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Edzio.Core.Models;

namespace Edzio.Core.Transfer;

public static class ChunkEngine
{
    public const int ChunkSize = 262144; // 256 KB

    public static async Task<FileEntry> BuildFileEntryAsync(string fullPath, string relativePath)
    {
        relativePath = relativePath.Replace('\\', '/');
        var fileInfo = new FileInfo(fullPath);
        var sizeBytes = fileInfo.Length;
        var chunks = new List<ChunkInfo>();

        await using var fs = File.OpenRead(fullPath);
        var buffer = new byte[ChunkSize];
        int chunkIndex = 0;

        while (true)
        {
            int bytesRead = await fs.ReadAsync(buffer, 0, ChunkSize);
            if (bytesRead == 0) break;

            var chunkBytes = buffer[..bytesRead];
            var hash = SHA256.HashData(chunkBytes);
            var sha256 = Convert.ToHexString(hash).ToLowerInvariant();

            chunks.Add(new ChunkInfo(chunkIndex, bytesRead, sha256));
            chunkIndex++;
        }

        return new FileEntry(relativePath, sizeBytes, chunks);
    }

    public static async IAsyncEnumerable<(int FileIndex, int ChunkIndex, byte[] Data)> ReadChunksAsync(
        string rootPath, TransferManifest manifest, HashSet<(int FileIndex, int ChunkIndex)> skipChunks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int fi = 0; fi < manifest.Files.Count; fi++)
        {
            var file = manifest.Files[fi];
            var filePath = Path.Combine(rootPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            await using var fs = File.OpenRead(filePath);
            var buffer = new byte[ChunkSize];

            for (int ci = 0; ci < file.Chunks.Count; ci++)
            {
                ct.ThrowIfCancellationRequested();
                int bytesRead = await fs.ReadAsync(buffer, 0, ChunkSize, ct);

                if (skipChunks.Contains((fi, ci)))
                    continue;

                var data = buffer[..bytesRead];
                yield return (fi, ci, data);
            }
        }
    }

    public static async Task WriteChunkAsync(string outputRoot, TransferManifest manifest,
        int fileIndex, int chunkIndex, byte[] data)
    {
        var tmpDir = Path.Combine(outputRoot, ".edzio-tmp", manifest.SessionId);
        Directory.CreateDirectory(tmpDir);
        var tmpPath = Path.Combine(tmpDir, $"{fileIndex}_{chunkIndex}.edztmp");
        await File.WriteAllBytesAsync(tmpPath, data);
    }

    public static async Task AssembleFileAsync(string outputRoot, TransferManifest manifest, int fileIndex)
    {
        var file = manifest.Files[fileIndex];
        var finalPath = Path.Combine(outputRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var finalDir = Path.GetDirectoryName(finalPath);
        if (finalDir != null)
            Directory.CreateDirectory(finalDir);

        var tmpDir = Path.Combine(outputRoot, ".edzio-tmp", manifest.SessionId);

        await using var outStream = File.Create(finalPath);

        for (int ci = 0; ci < file.Chunks.Count; ci++)
        {
            var tmpPath = Path.Combine(tmpDir, $"{fileIndex}_{ci}.edztmp");
            var chunkData = await File.ReadAllBytesAsync(tmpPath);

            var hash = SHA256.HashData(chunkData);
            var sha256 = Convert.ToHexString(hash).ToLowerInvariant();

            if (sha256 != file.Chunks[ci].Sha256)
                throw new InvalidDataException($"Chunk hash mismatch for file {fileIndex}, chunk {ci}");

            await outStream.WriteAsync(chunkData);
        }

        // Delete temp chunks after successful assembly
        for (int ci = 0; ci < file.Chunks.Count; ci++)
        {
            var tmpPath = Path.Combine(tmpDir, $"{fileIndex}_{ci}.edztmp");
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
    }
}
