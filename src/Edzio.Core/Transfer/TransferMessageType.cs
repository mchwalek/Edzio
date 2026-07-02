namespace Edzio.Core.Transfer;

/// <summary>First byte of every message on the transfer channel identifies its type.</summary>
public enum TransferMessageType : byte
{
    /// <summary>Payload: UTF-8 JSON of <see cref="Core.Models.TransferManifest"/>.</summary>
    Manifest = 0x01,

    /// <summary>Payload: UTF-8 JSON array of {fileIndex,chunkIndex} objects already received.</summary>
    Resume = 0x02,

    /// <summary>Payload: 4-byte LE file index + 4-byte LE chunk index + raw chunk bytes.</summary>
    Chunk = 0x03,

    /// <summary>Payload: empty. Signals all chunks have been sent.</summary>
    Done = 0x04,

    /// <summary>Payload: UTF-8 error message.</summary>
    Error = 0x05,
}
