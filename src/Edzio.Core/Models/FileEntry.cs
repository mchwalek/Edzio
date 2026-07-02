namespace Edzio.Core.Models;

/// <summary>Describes one file within a transfer manifest.</summary>
/// <param name="RelativePath">Forward-slash path relative to the transfer root.</param>
/// <param name="SizeBytes">Total file size in bytes.</param>
/// <param name="Chunks">Ordered list of chunks composing this file.</param>
public record FileEntry(string RelativePath, long SizeBytes, IReadOnlyList<ChunkInfo> Chunks);
