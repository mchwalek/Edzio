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
}
