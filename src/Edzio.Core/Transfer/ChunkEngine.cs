using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Edzio.Core.Models;

namespace Edzio.Core.Transfer;

public static class ChunkEngine
{
    /// <summary>
    /// Maximum number of file-data bytes per chunk (~256 KB).
    /// </summary>
    /// <remarks>
    /// Kept <see cref="TransferSession.ChunkHeaderSize"/> bytes below 262144 — SIPSorcery's
    /// fixed, non-configurable SCTP data channel maximum message size
    /// (<c>RTCSctpTransport.SCTP_DEFAULT_MAX_MESSAGE_SIZE</c>) — so that a full-size Chunk
    /// message (header + data) never exceeds the transport's hard limit. Raising this constant
    /// without also accounting for the header will cause sends to fail with
    /// "exceeded the maximum allowed message size".
    /// </remarks>
    public const int ChunkSize = 262144 - TransferSession.ChunkHeaderSize; // 262135 bytes

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

    /// <summary>
    /// Returns the absolute path of the final assembled file for
    /// <paramref name="fileIndex"/> under <paramref name="outputRoot"/>.
    /// </summary>
    public static string GetFinalFilePath(string outputRoot, TransferManifest manifest, int fileIndex)
        => Path.Combine(outputRoot,
            manifest.Files[fileIndex].RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Opens (or creates) the in-progress <c>.part</c> file for
    /// <paramref name="fileIndex"/>. Chunks are written directly into this single
    /// stream at their final offsets, so no per-chunk temp files
    /// and no final assembly copy pass are needed. The file persists across
    /// sessions, which is what makes resume work: already-received chunks are
    /// already at their final offsets.
    /// </summary>
    /// <remarks>
    /// Performance: the previous implementation wrote one temp file per chunk
    /// (create/write/close + <c>Directory.CreateDirectory</c> per chunk) and then
    /// re-read and re-wrote the entire payload during assembly. On the serial
    /// receive loop this per-chunk file churn backpressured the whole SCTP pipe
    /// (see docs/debug/slow-webrtc-transfer-throughput).
    /// </remarks>
    public static FileStream OpenPartStream(string outputRoot, TransferManifest manifest, int fileIndex)
    {
        var finalPath = GetFinalFilePath(outputRoot, manifest, fileIndex);
        var finalDir = Path.GetDirectoryName(finalPath);
        if (finalDir != null)
            Directory.CreateDirectory(finalDir);

        return new FileStream(finalPath + ".part", FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.None, bufferSize: 0, useAsync: true);
    }

    /// <summary>
    /// Verifies the chunk's SHA-256 against the manifest and writes it at its
    /// final offset in the file's <c>.part</c> stream. Throws
    /// <see cref="InvalidDataException"/> on hash mismatch (verification now
    /// happens on receipt instead of during a separate assembly pass).
    /// </summary>
    public static async Task WriteChunkAsync(FileStream partStream, TransferManifest manifest,
        int fileIndex, int chunkIndex, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var chunks = manifest.Files[fileIndex].Chunks;
        var expected = chunks[chunkIndex].Sha256;
        var actual = Convert.ToHexString(SHA256.HashData(data.Span)).ToLowerInvariant();
        if (actual != expected)
            throw new InvalidDataException($"Chunk hash mismatch for file {fileIndex}, chunk {chunkIndex}");

        // Offset is derived from the manifest's per-chunk sizes rather than
        // assuming every chunk is ChunkSize bytes — correct for any manifest.
        long offset = 0;
        for (int i = 0; i < chunkIndex; i++)
            offset += chunks[i].SizeBytes;

        partStream.Seek(offset, SeekOrigin.Begin);
        await partStream.WriteAsync(data, ct);
    }

    /// <summary>
    /// Promotes a completed <c>.part</c> file to its final name. The caller must
    /// have disposed the part stream first.
    /// </summary>
    public static void FinalizeFile(string outputRoot, TransferManifest manifest, int fileIndex)
    {
        var finalPath = GetFinalFilePath(outputRoot, manifest, fileIndex);
        File.Move(finalPath + ".part", finalPath, overwrite: true);
    }
}
