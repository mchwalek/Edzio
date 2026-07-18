using System.Net.Security;
using System.Net.Sockets;
using Edzio.Core.Transfer;

namespace Edzio.Core.Lan;

/// <summary>
/// LAN-direct <see cref="ITransferChannel"/> over an authenticated TLS stream.
/// Message framing is a 4-byte little-endian length prefix followed by the payload.
/// </summary>
/// <remarks>
/// Exists because SIPSorcery's managed SCTP sender caps WebRTC data-channel
/// throughput at roughly <c>4 packets × MTU / RTT</c> (~1 MB/s on a typical
/// Wi-Fi LAN — see docs/debug/slow-webrtc-transfer-throughput). Two peers on
/// the same LAN never needed WebRTC's NAT traversal in the first place; a
/// plain TLS socket runs at wire speed.
/// </remarks>
public sealed class TcpTransferChannel : ITransferChannel
{
    /// <summary>
    /// Upper bound on a single framed message, as a guard against a corrupt
    /// or hostile length prefix. Chunk messages are ~256 KB; manifests for
    /// very large file sets are the biggest legitimate messages.
    /// </summary>
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    private readonly TcpClient _client;
    private readonly SslStream _stream;

    /// <summary>
    /// Wraps an already-authenticated TLS stream. Callers use
    /// <see cref="LanDirect"/> to establish and authenticate the connection.
    /// </summary>
    internal TcpTransferChannel(TcpClient client, SslStream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <inheritdoc/>
    public Task WaitForOpenAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        var header = new byte[4];
        BitConverter.TryWriteBytes(header, data.Length);
        await _stream.WriteAsync(header, ct);
        await _stream.WriteAsync(data, ct);
        await _stream.FlushAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        await _stream.ReadExactlyAsync(header, ct);
        int length = BitConverter.ToInt32(header);

        if (length < 0 || length > MaxFrameBytes)
            throw new TransferException($"Invalid LAN frame length: {length}.");

        var payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        catch
        {
            // Best-effort close; the peer may already have gone away.
        }
        _client.Dispose();
    }
}
