namespace Edzio.Core.Models;

/// <summary>Describes all files in a transfer session, sent from sender to receiver at start.</summary>
/// <param name="SessionId">Stable UUID identifying this transfer (used for resume).</param>
/// <param name="TotalBytes">Sum of all file sizes.</param>
/// <param name="Files">Ordered list of files to transfer.</param>
public record TransferManifest(string SessionId, long TotalBytes, IReadOnlyList<FileEntry> Files);
