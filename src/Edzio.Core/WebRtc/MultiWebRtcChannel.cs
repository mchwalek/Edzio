using System.Threading.Channels;
using Edzio.Core.Signaling;
using Edzio.Core.Transfer;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Edzio.Core.WebRtc;

/// <summary>
/// An <see cref="ITransferChannel"/> that stripes data across several parallel WebRTC
/// data channels, each with its own SCTP association.
/// </summary>
/// <remarks>
/// SIPSorcery's SCTP congestion window stays pinned near its 4380-byte RFC 4960 initial
/// value, so one association is capped at roughly <c>4380 / RTT</c> regardless of the
/// link. Running several associations multiplies that ceiling.
///
/// Safe because chunk ordering is already irrelevant to the protocol: chunks carry
/// (fileIndex, chunkIndex), are written at manifest-derived offsets, and are SHA-256
/// verified individually on receipt. The single ordering constraint — Done arriving
/// after every chunk — is enforced by <see cref="FlushAsync"/>.
///
/// Used only on the WebRTC fallback path. The LAN-direct TCP channel already runs at
/// wire speed and gains nothing from striping.
/// </remarks>
internal sealed class MultiWebRtcChannel : ITransferChannel
{
    /// <summary>
    /// Number of parallel associations. Derived from the ~34 KB in flight needed to
    /// reach 6.75 MB/s at 5 ms RTT, divided by the ~4380-byte per-association window.
    /// Deliberately not user-configurable.
    /// </summary>
    internal const int DefaultLaneCount = 8;

    /// <summary>How often <see cref="FlushAsync"/> re-checks for a fully drained state.</summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly WebRtcChannel[] _lanes;
    private readonly IndexedSignalingClient[] _laneSignaling;
    private readonly ILogger<WebRtcChannel>? _logger;
    private readonly WebRtcRole _role;

    private readonly Channel<byte[]> _outbound;
    private readonly Channel<byte[]> _inbound;
    private readonly CancellationTokenSource _lifetime = new();

    private Task[] _pumps = [];
    private Task[] _mergers = [];
    private int _inFlight;

    /// <summary>
    /// Creates the lanes. Nothing connects until <see cref="ConnectAsync"/> is called.
    /// </summary>
    /// <param name="rtcConfig">ICE configuration, shared by every lane.</param>
    /// <param name="signaling">The shared signaling client. Not owned; never disposed here.</param>
    /// <param name="role">Offerer on the sending side, Answerer on the receiving side.</param>
    /// <param name="logger">Optional log sink.</param>
    /// <param name="laneCount">Number of parallel associations. Must match on both peers.</param>
    internal MultiWebRtcChannel(
        RTCConfiguration rtcConfig,
        ISignalingClient signaling,
        WebRtcRole role,
        ILogger<WebRtcChannel>? logger = null,
        int laneCount = DefaultLaneCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(laneCount, 1);

        _role = role;
        _logger = logger;
        _laneSignaling = new IndexedSignalingClient[laneCount];
        _lanes = new WebRtcChannel[laneCount];

        for (var i = 0; i < laneCount; i++)
        {
            _laneSignaling[i] = new IndexedSignalingClient(signaling, i);
            _lanes[i] = new WebRtcChannel(rtcConfig, _laneSignaling[i], role, logger);
        }

        // Bounded so backpressure reaches ChunkEngine.ReadChunksAsync. An unbounded
        // queue would buffer the whole file in memory.
        _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(laneCount * 2)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _inbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(laneCount * 2)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>
    /// Connects every lane concurrently and starts the send and receive workers.
    /// </summary>
    internal async Task ConnectAsync(CancellationToken ct)
    {
        Log($"[{_role}] connecting {_lanes.Length} lanes");
        await Task.WhenAll(_lanes.Select(lane => lane.ConnectAsync(ct)));

        _pumps = [.. _lanes.Select(StartPumpAsync)];
        _mergers = [.. _lanes.Select(StartMergeAsync)];
    }

    public Task WaitForOpenAsync(CancellationToken ct = default) =>
        Task.WhenAll(_lanes.Select(lane => lane.WaitForOpenAsync(ct)));

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        await WaitForOpenAsync(ct);
        await _outbound.Writer.WriteAsync(data, ct);
    }

    public Task<byte[]> ReceiveAsync(CancellationToken ct = default) =>
        _inbound.Reader.ReadAsync(ct).AsTask();

    /// <summary>
    /// Waits until the stripe queue is empty, no pump holds a message, and every lane's
    /// SCTP send buffer has drained.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var queued = _outbound.Reader.Count;
            var inFlight = Volatile.Read(ref _inFlight);
            var buffered = _lanes.Sum(lane => (double)lane.BufferedAmount);

            if (queued == 0 && inFlight == 0 && buffered == 0) return;

            await Task.Delay(DrainPollInterval, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();

        try
        {
            await Task.WhenAll(_pumps);
        }
        catch
        {
            // A failed lane is reported through ReceiveAsync; nothing to add here.
        }

        await _lifetime.CancelAsync();

        try
        {
            await Task.WhenAll(_mergers);
        }
        catch
        {
            // Expected on cancellation.
        }

        foreach (var lane in _lanes) await lane.DisposeAsync();
        foreach (var signaling in _laneSignaling) await signaling.DisposeAsync();

        _inbound.Writer.TryComplete();
        _lifetime.Dispose();
    }

    /// <summary>
    /// Drains the shared queue into one lane. Work-stealing falls out of the shared
    /// queue: a temporarily slow lane simply takes fewer messages, where a round-robin
    /// assignment would head-of-line block on it.
    /// </summary>
    private async Task StartPumpAsync(WebRtcChannel lane)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(_lifetime.Token))
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                await lane.SendAsync(message, _lifetime.Token);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>
    /// Merges one lane's inbound messages into the shared receive queue. A lane failing
    /// faults the queue so the transfer surfaces the error rather than hanging.
    /// </summary>
    private async Task StartMergeAsync(WebRtcChannel lane)
    {
        try
        {
            while (!_lifetime.Token.IsCancellationRequested)
            {
                var message = await lane.ReceiveAsync(_lifetime.Token);
                await _inbound.Writer.WriteAsync(message, _lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose.
        }
        catch (Exception ex)
        {
            _inbound.Writer.TryComplete(ex);
        }
    }

    private void Log(string msg) => _logger?.LogInformation("{Msg}", msg);
}
