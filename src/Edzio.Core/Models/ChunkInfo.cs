namespace Edzio.Core.Models;

/// <summary>Describes a single chunk within a file transfer.</summary>
/// <param name="Index">Zero-based chunk index within the file.</param>
/// <param name="SizeBytes">Byte count of this chunk (last chunk may be smaller).</param>
/// <param name="Sha256">Hex-encoded SHA-256 hash of the chunk bytes.</param>
public record ChunkInfo(int Index, int SizeBytes, string Sha256);
