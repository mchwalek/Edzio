using System.Runtime.ExceptionServices;
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

    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for the pumps to finish on their own
    /// before cancelling them, mirroring <c>WebRtcChannel.StallTimeout</c>. Bounds
    /// disposal for a lane stuck inside <c>WaitForSendBufferSpaceAsync</c> whose send
    /// buffer never drains (stalled, not failed) — without this, that pump's
    /// cancellation-unblock only happens after the very await it would need to
    /// unblock, so disposal would otherwise hang forever.
    /// </summary>
    private static readonly TimeSpan DisposeStallTimeout = TimeSpan.FromSeconds(15);

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
    /// The first exception thrown by a lane's send, if any. Set at most once (first
    /// fault wins) and rethrown to the caller of <see cref="SendAsync"/> or
    /// <see cref="FlushAsync"/> instead of being silently dropped.
    /// </summary>
    private ExceptionDispatchInfo? _pumpFault;

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

    /// <inheritdoc/>
    public Task WaitForOpenAsync(CancellationToken ct = default) =>
        Task.WhenAll(_lanes.Select(lane => lane.WaitForOpenAsync(ct)));

    /// <inheritdoc/>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        ThrowIfPumpFaulted();
        await WaitForOpenAsync(ct);
        await _outbound.Writer.WriteAsync(data, ct);
        ThrowIfPumpFaulted();
    }

    /// <inheritdoc/>
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
            ThrowIfPumpFaulted();

            var queued = _outbound.Reader.Count;
            var inFlight = Volatile.Read(ref _inFlight);
            var buffered = _lanes.Sum(lane => (double)lane.BufferedAmount);

            if (queued == 0 && inFlight == 0 && buffered == 0) return;

            await Task.Delay(DrainPollInterval, ct);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();

        // Give the pumps a bounded window to finish on their own — this is what
        // preserves in-flight message delivery for a normal (non-stalled) shutdown,
        // since cancelling _lifetime here would abort a send that was about to
        // succeed. Only once that window elapses (a stalled, not failed, lane) do we
        // cancel to unblock it; see DisposeStallTimeout.
        var pumpsTask = Task.WhenAll(_pumps);
        var pumpsCompletedInTime = await WaitForPumpsOrStallAsync(pumpsTask, DisposeStallTimeout, Task.Delay);
        if (!pumpsCompletedInTime)
        {
            Log($"[{_role}] pump drain stalled past {DisposeStallTimeout.TotalSeconds}s during " +
                "dispose — cancelling and closing anyway.");
        }

        await _lifetime.CancelAsync();

        try
        {
            // If the pumps already finished above, this resolves instantly. If they
            // stalled, cancelling _lifetime just now unblocks whichever pump was stuck
            // in WaitForSendBufferSpaceAsync, so this now completes too.
            await pumpsTask;
        }
        catch
        {
            // Expected on cancellation.
        }

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
    /// Waits for <paramref name="pumps"/> to finish, but gives up and returns
    /// <see langword="false"/> once <paramref name="stallTimeout"/> elapses — so a
    /// lane whose send buffer never drains cannot hang the wait forever. Takes
    /// <paramref name="delay"/> as a parameter (rather than calling
    /// <see cref="Task.Delay(TimeSpan)"/> directly) so tests can simulate the timeout
    /// elapsing instantly instead of waiting out a real 15 seconds, mirroring
    /// <c>WebRtcChannel.WaitForDrainOrStallAsync</c>.
    /// </summary>
    internal static async Task<bool> WaitForPumpsOrStallAsync(
        Task pumps, TimeSpan stallTimeout, Func<TimeSpan, Task> delay)
    {
        var timeoutTask = delay(stallTimeout);
        var finished = await Task.WhenAny(pumps, timeoutTask);
        return finished == pumps;
    }

    /// <summary>
    /// Drains the shared queue into one lane. Work-stealing falls out of the shared
    /// queue: a temporarily slow lane simply takes fewer messages, where a round-robin
    /// assignment would head-of-line block on it.
    /// </summary>
    /// <remarks>
    /// A send failure on this lane (a realistic WAN failure, not just a shutdown
    /// cancellation) is captured into <see cref="_pumpFault"/> — the first one wins —
    /// and this pump then stops taking further work, instead of leaving the task to
    /// fault silently and unobserved. <see cref="SendAsync"/> and
    /// <see cref="FlushAsync"/> check and rethrow it, so a lost chunk surfaces to the
    /// caller rather than <see cref="FlushAsync"/> reporting a false all-clear.
    /// </remarks>
    private async Task StartPumpAsync(WebRtcChannel lane)
    {
        try
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
        catch (OperationCanceledException) when (_lifetime.Token.IsCancellationRequested)
        {
            // Expected on dispose/cancellation — not a fault.
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _pumpFault, ExceptionDispatchInfo.Capture(ex), null);
        }
    }

    /// <summary>Rethrows the first captured pump fault, if any. See <see cref="_pumpFault"/>.</summary>
    private void ThrowIfPumpFaulted() => Volatile.Read(ref _pumpFault)?.Throw();

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
