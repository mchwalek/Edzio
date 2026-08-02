namespace Edzio.Core.Transfer;

/// <summary>
/// Abstraction over the P2P data transport (WebRTC data channel).
/// Binary messages are framed and ordered; delivery is reliable.
/// </summary>
public interface ITransferChannel : IAsyncDisposable
{
    /// <summary>Sends a binary message to the remote peer.</summary>
    Task SendAsync(byte[] data, CancellationToken ct = default);

    /// <summary>Receives the next binary message from the remote peer. Blocks until a message arrives.</summary>
    Task<byte[]> ReceiveAsync(CancellationToken ct = default);

    /// <summary>Waits until the channel is open and ready to send/receive.</summary>
    Task WaitForOpenAsync(CancellationToken ct = default);

    /// <summary>
    /// Waits until every message already handed to <see cref="SendAsync"/> has left
    /// this channel.
    /// </summary>
    /// <remarks>
    /// A single ordered transport delivers in submission order, so the default is a
    /// no-op. Transports that stripe across several independent connections must
    /// override this: without it the terminating Done message can overtake chunks
    /// still queued on a slower connection, and the receiver finalizes a partial file.
    /// </remarks>
    Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}
