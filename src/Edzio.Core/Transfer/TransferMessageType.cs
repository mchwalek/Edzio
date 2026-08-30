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

    /// <summary>
    /// Payload: 4-byte LE totalParts + 4-byte LE partIndex + UTF-8 JSON slice of
    /// <see cref="Core.Models.TransferManifest"/>. The manifest grows with chunk
    /// count (~110 bytes/chunk of per-chunk SHA-256 JSON), so large files can
    /// exceed a single message's size limit (262,144 bytes on WebRTC data
    /// channels); it is fragmented into a sequence of these and reassembled by
    /// the receiver before deserializing.
    /// </summary>
    ManifestChunk = 0x06,

    /// <summary>
    /// Payload: 4-byte LE totalParts + 4-byte LE partIndex + UTF-8 JSON slice of
    /// the resume list (array of {fileIndex,chunkIndex}). Fragmented for the
    /// same reason as <see cref="ManifestChunk"/> — a resumed transfer with many
    /// already-received chunks can also exceed a single message's size limit.
    /// </summary>
    ResumeChunk = 0x07,

    /// <summary>
    /// Payload: 4-byte LE lane index. Sent by <c>MultiWebRtcChannel.FlushAsync</c> down
    /// one specific lane, pinned (not through the striping pump), after that lane's
    /// local SCTP send buffer has drained. Intercepted by the peer's merge loop —
    /// never surfaces to <see cref="TransferSession"/> — which responds with
    /// <see cref="FlushAck"/> on the same lane. Because SCTP delivers in order within
    /// an association, receiving this proves every chunk previously sent on this lane
    /// has actually left the sender — but not yet that the peer received it; that is
    /// what the round trip to <see cref="FlushAck"/> proves.
    /// </summary>
    FlushMarker = 0x08,

    /// <summary>
    /// Payload: 4-byte LE lane index, echoed back from the matching
    /// <see cref="FlushMarker"/>. Proves the peer has received every chunk sent on
    /// this lane before the marker, closing the gap that local-buffer-only draining
    /// left open under real network loss (see <c>MultiWebRtcChannel.FlushAsync</c>).
    /// Intercepted by the sender's merge loop, never surfaces to
    /// <see cref="TransferSession"/>.
    /// </summary>
    FlushAck = 0x09,

    /// <summary>Sender's transfer offer: sender name and file list, sent before any manifest/chunk data. Payload: UTF-8 JSON of <see cref="TransferOffer"/>.</summary>
    Offer = 0x0A,

    /// <summary>Receiver's accept/decline response to an <see cref="Offer"/>. Payload: 1 byte, 1 = accept, 0 = decline.</summary>
    OfferResponse = 0x0B,
}
