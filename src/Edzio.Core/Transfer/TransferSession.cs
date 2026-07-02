using System.Text.Json;
using Edzio.Core.Models;
using Edzio.Core.Persistence;

namespace Edzio.Core.Transfer;

public static class TransferSession
{
    // ── Wire-format helpers ──────────────────────────────────────────────

    /// <summary>Builds a Manifest message: [0x01][UTF-8 JSON]</summary>
    private static byte[] BuildManifestMessage(TransferManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var msg  = new byte[1 + json.Length];
        msg[0]   = (byte)TransferMessageType.Manifest;
        json.CopyTo(msg, 1);
        return msg;
    }

    /// <summary>
    /// Builds a Chunk message: [0x03][4-byte LE fileIndex][4-byte LE chunkIndex][data]
    /// </summary>
    private static byte[] BuildChunkMessage(int fileIndex, int chunkIndex, byte[] data)
    {
        var msg = new byte[1 + 4 + 4 + data.Length];
        msg[0]  = (byte)TransferMessageType.Chunk;
        WriteInt32LE(msg, 1, fileIndex);
        WriteInt32LE(msg, 5, chunkIndex);
        data.CopyTo(msg, 9);
        return msg;
    }

    /// <summary>Builds a Done message: [0x04]</summary>
    private static byte[] BuildDoneMessage() => new[] { (byte)TransferMessageType.Done };

    private static void WriteInt32LE(byte[] buf, int offset, int value)
    {
        buf[offset + 0] = (byte)(value);
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    // ── Resume parsing ──────────────────────────────────────────────────

    private record ResumeEntry(int FileIndex, int ChunkIndex);

    private static HashSet<(int FileIndex, int ChunkIndex)> ParseResumeMessage(byte[] message)
    {
        // message[0] == 0x02, remainder is UTF-8 JSON array of {fileIndex,chunkIndex}
        var json    = message.AsSpan(1);
        var entries = JsonSerializer.Deserialize<List<ResumeEntry>>(json)
                      ?? new List<ResumeEntry>();

        return entries
            .Select(e => (e.FileIndex, e.ChunkIndex))
            .ToHashSet();
    }

    // ── SendAsync ───────────────────────────────────────────────────────

    public static async Task SendAsync(
        string sourceRoot,
        TransferManifest manifest,
        ITransferChannel channel,
        TransferRepository repository,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Wait until the data channel is open
        await channel.WaitForOpenAsync(ct);

        // 2. Persist the session
        var manifestJson = JsonSerializer.Serialize(manifest);
        await repository.SaveSessionAsync(
            manifest.SessionId,
            peerName: "peer",
            direction: TransferDirection.Send,
            manifestJson: manifestJson,
            status: TransferStatus.InProgress);

        // 3. Send manifest
        var manifestMsg = BuildManifestMessage(manifest);
        await channel.SendAsync(manifestMsg, ct);

        // 4. Receive Resume (0x02) — or Error (0x05)
        var resumeMsg = await channel.ReceiveAsync(ct);
        if (resumeMsg.Length == 0)
            throw new TransferException("Received empty message while waiting for Resume.");

        if (resumeMsg[0] == (byte)TransferMessageType.Error)
            throw new TransferException("Receiver signalled an error after manifest.");

        if (resumeMsg[0] != (byte)TransferMessageType.Resume)
            throw new TransferException(
                $"Expected Resume (0x02) but got 0x{resumeMsg[0]:X2}.");

        var skipChunks = ParseResumeMessage(resumeMsg);

        // 5. Stream chunks
        int totalChunks = manifest.Files.Sum(f => f.Chunks.Count);
        int chunksComplete = 0;
        long bytesSent = 0;

        await foreach (var (fileIndex, chunkIndex, data) in
            ChunkEngine.ReadChunksAsync(sourceRoot, manifest, skipChunks, ct))
        {
            var chunkMsg = BuildChunkMessage(fileIndex, chunkIndex, data);
            await channel.SendAsync(chunkMsg, ct);

            bytesSent += data.Length;
            chunksComplete++;

            progress?.Report(new TransferProgress(
                BytesSent:      bytesSent,
                TotalBytes:     manifest.TotalBytes,
                ChunksComplete: chunksComplete,
                ChunksTotal:    totalChunks));
        }

        // 6. Send Done
        await channel.SendAsync(BuildDoneMessage(), ct);

        // 7. Mark session completed
        await repository.UpdateStatusAsync(manifest.SessionId, TransferStatus.Completed);
    }

    // ── ReceiveAsync ─────────────────────────────────────────────────────

    public static async Task<TransferManifest> ReceiveAsync(
        string outputRoot,
        string peerName,
        ITransferChannel channel,
        TransferRepository repository,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Wait for the channel to be open
        await channel.WaitForOpenAsync(ct);

        // 2. Read first message — must be Manifest (0x01)
        byte[] manifestMsg = await channel.ReceiveAsync(ct);
        if (manifestMsg.Length < 1 || manifestMsg[0] != (byte)TransferMessageType.Manifest)
            throw new TransferException("Expected Manifest message as first message.");

        string manifestJson = System.Text.Encoding.UTF8.GetString(manifestMsg, 1, manifestMsg.Length - 1);
        TransferManifest manifest = JsonSerializer.Deserialize<TransferManifest>(manifestJson)
            ?? throw new TransferException("Failed to deserialize TransferManifest.");

        // 3. Load already-received chunks from the repository
        IReadOnlyList<(int FileIndex, int ChunkIndex)> existingChunks =
            await repository.GetReceivedChunksAsync(manifest.SessionId);
        var receivedSet = new HashSet<(int, int)>(existingChunks);

        // 4. Save/update session in repository
        await repository.SaveSessionAsync(
            manifest.SessionId,
            peerName,
            TransferDirection.Receive,
            manifestJson,
            TransferStatus.InProgress);

        // 5. Build and send Resume message (0x02)
        string resumeJson = JsonSerializer.Serialize(
            existingChunks.Select(c => new { fileIndex = c.FileIndex, chunkIndex = c.ChunkIndex }));
        byte[] resumePayload = System.Text.Encoding.UTF8.GetBytes(resumeJson);
        byte[] resumeMsg = new byte[1 + resumePayload.Length];
        resumeMsg[0] = (byte)TransferMessageType.Resume;
        resumePayload.CopyTo(resumeMsg, 1);
        await channel.SendAsync(resumeMsg, ct);

        // 6. Receive chunks until Done (0x04)
        int totalChunks = manifest.Files.Sum(f => f.Chunks.Count);
        long bytesReceived = existingChunks.Sum(c =>
        {
            var file = manifest.Files[c.FileIndex];
            return (long)file.Chunks[c.ChunkIndex].SizeBytes;
        });
        int chunksComplete = existingChunks.Count;

        while (true)
        {
            byte[] msg = await channel.ReceiveAsync(ct);
            if (msg.Length < 1)
                continue;

            var msgType = (TransferMessageType)msg[0];

            switch (msgType)
            {
                case TransferMessageType.Done:
                    goto afterLoop;

                case TransferMessageType.Chunk:
                {
                    // [0x03][4-byte LE fileIndex][4-byte LE chunkIndex][data...]
                    if (msg.Length < 9)
                        throw new TransferException("Malformed Chunk message: too short.");

                    int fileIndex  = BitConverter.ToInt32(msg, 1);
                    int chunkIndex = BitConverter.ToInt32(msg, 5);
                    byte[] data    = msg[9..];

                    await ChunkEngine.WriteChunkAsync(outputRoot, manifest, fileIndex, chunkIndex, data);
                    await repository.MarkChunkReceivedAsync(manifest.SessionId, fileIndex, chunkIndex);

                    receivedSet.Add((fileIndex, chunkIndex));
                    chunksComplete++;
                    bytesReceived += data.Length;

                    progress?.Report(new TransferProgress(bytesReceived, manifest.TotalBytes, chunksComplete, totalChunks));
                    break;
                }

                case TransferMessageType.Error:
                {
                    string errorMsg = System.Text.Encoding.UTF8.GetString(msg, 1, msg.Length - 1);
                    throw new TransferException($"Sender reported error: {errorMsg}");
                }

                default:
                    // Ignore unknown message types
                    break;
            }
        }
        afterLoop:

        // 7. Assemble each file from its chunks
        for (int i = 0; i < manifest.Files.Count; i++)
            await ChunkEngine.AssembleFileAsync(outputRoot, manifest, i);

        // 8. Mark session completed
        await repository.UpdateStatusAsync(manifest.SessionId, TransferStatus.Completed);

        // 9. Return manifest
        return manifest;
    }
}
