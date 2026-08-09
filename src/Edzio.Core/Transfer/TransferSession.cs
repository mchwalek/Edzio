using System.Text.Json;
using Edzio.Core.Models;
using Edzio.Core.Persistence;

namespace Edzio.Core.Transfer;

public static class TransferSession
{
    // ── Wire-format helpers ──────────────────────────────────────────────

    /// <summary>
    /// Size in bytes of the Chunk message header: 1-byte <see cref="TransferMessageType"/>
    /// + 4-byte LE fileIndex + 4-byte LE chunkIndex. <see cref="ChunkEngine.ChunkSize"/> is
    /// derived from this so that a full-size chunk message never exceeds the WebRTC data
    /// channel's maximum message size.
    /// </summary>
    public const int ChunkHeaderSize = 1 + 4 + 4;

    /// <summary>
    /// Size in bytes of a fragment message header: 1-byte <see cref="TransferMessageType"/>
    /// + 4-byte LE totalParts + 4-byte LE partIndex. Used by <see cref="ManifestChunk"/>-
    /// and <see cref="ResumeChunk"/>-typed messages (see <see cref="SendFragmentedAsync"/>).
    /// </summary>
    private const int FragmentHeaderSize = 1 + 4 + 4;

    /// <summary>
    /// Maximum JSON payload bytes per fragment. The Manifest and Resume messages
    /// grow with chunk count (~110 bytes/chunk of per-chunk SHA-256 JSON) and can
    /// exceed a single message's size limit for large files (SIPSorcery's
    /// 262,144-byte SCTP data-channel cap — see <see cref="ChunkEngine.ChunkSize"/>),
    /// so they are split into a sequence of fragments this size or smaller.
    /// </summary>
    private const int FragmentMaxPayloadBytes = 262144 - FragmentHeaderSize;

    /// <summary>
    /// Sanity bound on the fragment count a peer will accept, to reject a
    /// corrupt/malicious totalParts header before allocating an array sized by it.
    /// 10,000,000 parts is already far beyond any plausible manifest/resume size.
    /// </summary>
    private const int MaxFragmentParts = 10_000_000;

    /// <summary>
    /// Splits <paramref name="jsonBytes"/> into a sequence of <paramref name="type"/>-typed
    /// fragment messages: <c>[type][4-byte LE totalParts][4-byte LE partIndex][slice]</c>,
    /// and sends them in order.
    /// </summary>
    private static async Task SendFragmentedAsync(
        ITransferChannel channel, TransferMessageType type, byte[] jsonBytes, CancellationToken ct)
    {
        int totalParts = Math.Max(1,
            (jsonBytes.Length + FragmentMaxPayloadBytes - 1) / FragmentMaxPayloadBytes);

        for (int part = 0; part < totalParts; part++)
        {
            int offset = part * FragmentMaxPayloadBytes;
            int length = Math.Min(FragmentMaxPayloadBytes, jsonBytes.Length - offset);

            var msg = new byte[FragmentHeaderSize + length];
            msg[0] = (byte)type;
            WriteInt32LE(msg, 1, totalParts);
            WriteInt32LE(msg, 5, part);
            Buffer.BlockCopy(jsonBytes, offset, msg, FragmentHeaderSize, length);

            await channel.SendAsync(msg, ct);
        }
    }

    /// <summary>
    /// Reassembles a fragmented <paramref name="expectedType"/> message.
    /// <paramref name="firstMessage"/> is the first fragment (already read by the
    /// caller in order to detect the message type); any remaining fragments are
    /// read from <paramref name="channel"/>. Validates a consistent totalParts
    /// across fragments, in-range/non-duplicate part indices, and a sane fragment
    /// count, then returns the concatenated JSON payload in part order.
    /// </summary>
    private static async Task<byte[]> ReceiveFragmentedAsync(
        ITransferChannel channel, TransferMessageType expectedType, byte[] firstMessage, CancellationToken ct)
    {
        if (firstMessage.Length < FragmentHeaderSize || firstMessage[0] != (byte)expectedType)
            throw new TransferException(
                $"Expected {expectedType} (0x{(byte)expectedType:X2}) but got " +
                $"0x{(firstMessage.Length > 0 ? firstMessage[0] : 0):X2}.");

        int totalParts = BitConverter.ToInt32(firstMessage, 1);
        if (totalParts is < 1 or > MaxFragmentParts)
            throw new TransferException($"Invalid fragment count {totalParts} for {expectedType}.");

        var parts = new byte[totalParts][];

        void Store(byte[] msg)
        {
            int partIndex = BitConverter.ToInt32(msg, 5);
            if (partIndex < 0 || partIndex >= totalParts)
                throw new TransferException(
                    $"Fragment part index {partIndex} out of range (0..{totalParts - 1}) for {expectedType}.");
            if (parts[partIndex] is not null)
                throw new TransferException($"Duplicate fragment part index {partIndex} for {expectedType}.");
            parts[partIndex] = msg[FragmentHeaderSize..];
        }

        Store(firstMessage);
        for (int received = 1; received < totalParts; received++)
        {
            byte[] msg = await channel.ReceiveAsync(ct);
            if (msg.Length < FragmentHeaderSize || msg[0] != (byte)expectedType)
                throw new TransferException(
                    $"Expected continuation of {expectedType} but got " +
                    $"0x{(msg.Length > 0 ? msg[0] : 0):X2}.");

            int msgTotalParts = BitConverter.ToInt32(msg, 1);
            if (msgTotalParts != totalParts)
                throw new TransferException(
                    $"Inconsistent fragment count for {expectedType}: expected {totalParts}, got {msgTotalParts}.");

            Store(msg);
        }

        var result = new byte[parts.Sum(p => p.Length)];
        int writeOffset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, writeOffset);
            writeOffset += p.Length;
        }
        return result;
    }

    /// <summary>
    /// Builds a Chunk message: [0x03][4-byte LE fileIndex][4-byte LE chunkIndex][data]
    /// </summary>
    private static byte[] BuildChunkMessage(int fileIndex, int chunkIndex, byte[] data)
    {
        var msg = new byte[ChunkHeaderSize + data.Length];
        msg[0]  = (byte)TransferMessageType.Chunk;
        WriteInt32LE(msg, 1, fileIndex);
        WriteInt32LE(msg, 5, chunkIndex);
        data.CopyTo(msg, ChunkHeaderSize);
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

    /// <summary>
    /// Options for deserializing the Resume payload, which is emitted as a
    /// camelCase anonymous type (<c>{"fileIndex":.., "chunkIndex":..}</c>) —
    /// case-insensitive matching is required because
    /// <see cref="JsonSerializer.Deserialize{TValue}(byte[], JsonSerializerOptions)"/>
    /// is case-sensitive by default and would otherwise silently bind every
    /// entry's FileIndex/ChunkIndex to 0 (found via a test exercising a skip
    /// set other than the coincidentally-matching (0,0) — see
    /// FragmentedMessageTests.DEBUG_SmallSkipSet, since removed. This was a
    /// pre-existing bug, not introduced by fragmentation.)
    /// </summary>
    private static readonly JsonSerializerOptions ResumeEntryOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses the reassembled Resume payload: a UTF-8 JSON array of {fileIndex,chunkIndex}.</summary>
    private static HashSet<(int FileIndex, int ChunkIndex)> ParseResumePayload(byte[] json)
    {
        var entries = JsonSerializer.Deserialize<List<ResumeEntry>>(json, ResumeEntryOptions)
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
        var manifestJsonBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        await repository.SaveSessionAsync(
            manifest.SessionId,
            peerName: "peer",
            direction: TransferDirection.Send,
            manifestJson: System.Text.Encoding.UTF8.GetString(manifestJsonBytes),
            status: TransferStatus.InProgress);

        // 3. Send manifest (fragmented — large file counts can exceed one message)
        await SendFragmentedAsync(channel, TransferMessageType.ManifestChunk, manifestJsonBytes, ct);

        // 4. Receive Resume (0x07, possibly fragmented) — or Error (0x05)
        var resumeMsg = await channel.ReceiveAsync(ct);
        if (resumeMsg.Length == 0)
            throw new TransferException("Received empty message while waiting for Resume.");

        if (resumeMsg[0] == (byte)TransferMessageType.Error)
            throw new TransferException("Receiver signalled an error after manifest.");

        var resumeJsonBytes = await ReceiveFragmentedAsync(channel, TransferMessageType.ResumeChunk, resumeMsg, ct);
        var skipChunks = ParseResumePayload(resumeJsonBytes);

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

        // Barrier before Done. A striping transport may still have chunks queued on a
        // slower connection; Done overtaking them would make the receiver finalize an
        // incomplete file. Single-connection transports no-op here.
        await channel.FlushAsync(ct);

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

        // 2. Read first message — must be the start of a Manifest (0x06, possibly fragmented)
        byte[] firstManifestMsg = await channel.ReceiveAsync(ct);
        byte[] manifestJsonBytes = await ReceiveFragmentedAsync(
            channel, TransferMessageType.ManifestChunk, firstManifestMsg, ct);

        string manifestJson = System.Text.Encoding.UTF8.GetString(manifestJsonBytes);
        TransferManifest manifest = JsonSerializer.Deserialize<TransferManifest>(manifestJsonBytes)
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

        // 5. Build and send Resume message (0x07, fragmented — large resume sets
        // can exceed one message just like the manifest)
        byte[] resumeJsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            existingChunks.Select(c => new { fileIndex = c.FileIndex, chunkIndex = c.ChunkIndex }));
        await SendFragmentedAsync(channel, TransferMessageType.ResumeChunk, resumeJsonBytes, ct);

        // 6. Receive chunks until Done (0x04)
        int totalChunks = manifest.Files.Sum(f => f.Chunks.Count);
        long bytesReceived = existingChunks.Sum(c =>
        {
            var file = manifest.Files[c.FileIndex];
            return (long)file.Chunks[c.ChunkIndex].SizeBytes;
        });
        int chunksComplete = existingChunks.Count;

        // One kept-open .part stream per file — chunks are written at their final
        // offsets, so there are no per-chunk temp files and no assembly copy pass.
        // Chunk-received DB marks are batched: a per-chunk SELECT + INSERT + commit
        // stalls the (fully serial) receive loop, which backpressures the sender
        // through SCTP flow control (see docs/debug/slow-webrtc-transfer-throughput).
        //
        // Opened eagerly for every file (not lazily on first chunk): a zero-chunk
        // file (an empty source file) never gets a Chunk message, so a lazy-open
        // keyed off the Chunk branch would leave it with no .part file at all,
        // and FinalizeFile's unconditional File.Move would throw FileNotFoundException.
        var partStreams = new Dictionary<int, FileStream>();
        for (int fi = 0; fi < manifest.Files.Count; fi++)
            partStreams[fi] = ChunkEngine.OpenPartStream(outputRoot, manifest, fi);

        var pendingMarks  = new List<(int FileIndex, int ChunkIndex)>();
        const int MarkFlushBatchSize = 16;

        try
        {
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
                        var data       = msg.AsMemory(9);

                        if (!partStreams.TryGetValue(fileIndex, out var partStream))
                        {
                            partStream = ChunkEngine.OpenPartStream(outputRoot, manifest, fileIndex);
                            partStreams[fileIndex] = partStream;
                        }

                        await ChunkEngine.WriteChunkAsync(partStream, manifest, fileIndex, chunkIndex, data, ct);

                        // Only queue a DB mark the first time we see this chunk —
                        // re-sent chunks are rewritten (harmless, idempotent) but must
                        // not produce duplicate rows.
                        if (receivedSet.Add((fileIndex, chunkIndex)))
                        {
                            pendingMarks.Add((fileIndex, chunkIndex));
                            if (pendingMarks.Count >= MarkFlushBatchSize)
                            {
                                await repository.MarkChunksReceivedAsync(manifest.SessionId, pendingMarks);
                                pendingMarks.Clear();
                            }
                        }

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
        }
        finally
        {
            // Persist any unflushed marks and release file handles even on
            // failure/cancel — the .part files and DB marks are what make resume work.
            if (pendingMarks.Count > 0)
                await repository.MarkChunksReceivedAsync(manifest.SessionId, pendingMarks);
            foreach (var stream in partStreams.Values)
                await stream.DisposeAsync();
        }
        afterLoop:

        // 7. Verify completeness, then promote each .part file to its final name.
        // (Chunk hashes were already verified on receipt in WriteChunkAsync.)
        for (int fi = 0; fi < manifest.Files.Count; fi++)
        {
            for (int ci = 0; ci < manifest.Files[fi].Chunks.Count; ci++)
            {
                if (!receivedSet.Contains((fi, ci)))
                    throw new TransferException(
                        $"Sender signalled Done but chunk {ci} of file {fi} was never received.");
            }

            ChunkEngine.FinalizeFile(outputRoot, manifest, fi);
        }

        // 8. Mark session completed
        await repository.UpdateStatusAsync(manifest.SessionId, TransferStatus.Completed);

        // 9. Return manifest
        return manifest;
    }
}
