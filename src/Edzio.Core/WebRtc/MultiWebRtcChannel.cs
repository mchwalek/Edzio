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

    /// <summary>
    /// How long <see cref="FlushAsync"/> waits for every lane's <c>FlushAck</c> before
    /// giving up and throwing. Same value as <see cref="DisposeStallTimeout"/> — both
    /// bound a wait for the peer under adverse network conditions.
    /// </summary>
    private static readonly TimeSpan FlushBarrierTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long <see cref="ReceiveAsync"/> will wait for an inbound message before
    /// faulting with a <see cref="TransferException"/>. Used to break SCTP head-of-line
    /// blocking on ordered channels when chunks are lost.
    /// </summary>
    private static readonly TimeSpan ReceiveIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly WebRtcChannel[] _lanes;
    private readonly IndexedSignalingClient[] _laneSignaling;
    private readonly ILogger<WebRtcChannel>? _logger;
    private readonly WebRtcRole _role;
    private readonly ISignalingClient _signaling;

    private readonly Channel<byte[]> _outbound;
    private readonly Channel<byte[]> _inbound;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _receiveCts = new();

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
    /// Set by <see cref="FlushAsync"/> before it sends any marker, cleared when it
    /// returns. <see langword="null"/> at all other times. The merge loop
    /// (<see cref="StartMergeAsync"/>) completes <c>_pendingFlushAcks[laneIndex]</c>
    /// when that lane's <c>FlushAck</c> arrives. Always written before any marker can
    /// possibly produce a matching ack, so no synchronization beyond
    /// <see cref="Volatile"/> is needed.
    /// </summary>
    private TaskCompletionSource<bool>[]? _pendingFlushAcks;

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
        _signaling = signaling;
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

        // Hook into the signaling peer disconnected event to cancel any active receive
        _signaling.PeerDisconnected += OnPeerDisconnected;
        
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
        ReceiveOrTimeoutAsync(_inbound.Reader, ct, _receiveCts.Token, ReceiveIdleTimeout, Task.Delay);

    /// <summary>
    /// Waits for the next inbound message, but gives up and throws a
    /// <see cref="TransferException"/> once <paramref name="idleTimeout"/> elapses
    /// with no message — breaking SCTP head-of-line blocking on ordered channels
    /// when chunks are lost. Cancelling <paramref name="ct"/> or
    /// <paramref name="disconnectedCt"/> (peer disconnected) instead propagates a
    /// plain <see cref="OperationCanceledException"/>, unwrapped, so callers can
    /// tell a genuine cancellation apart from a stall. Takes <paramref name="delay"/>
    /// as a parameter (rather than calling <see cref="Task.Delay(TimeSpan)"/>
    /// directly) so tests can simulate the timeout elapsing instantly instead of
    /// waiting out a real 30 seconds, mirroring <see cref="WaitForPumpsOrStallAsync"/>.
    /// </summary>
    internal static async Task<byte[]> ReceiveOrTimeoutAsync(
        ChannelReader<byte[]> inbound,
        CancellationToken ct,
        CancellationToken disconnectedCt,
        TimeSpan idleTimeout,
        Func<TimeSpan, Task> delay)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, disconnectedCt);
        var receiveTask = inbound.ReadAsync(linkedCts.Token).AsTask();
        var timeoutTask = delay(idleTimeout);

        var finished = await Task.WhenAny(receiveTask, timeoutTask);
        if (finished == receiveTask) return await receiveTask;

        // Unblock the still-pending read; its eventual cancellation is expected
        // and not observed further.
        await linkedCts.CancelAsync();
        throw new TransferException(
            $"Receive stalled for {idleTimeout.TotalSeconds}s with no inbound messages. " +
            "This may be due to SCTP ordered delivery blocking on lost chunks.");
    }

    /// <summary>
    /// Waits until every chunk sent on every lane has been received by the peer —
    /// not merely handed to this machine's local SCTP send buffer.
    /// </summary>
    /// <remarks>
    /// Draining the local send buffers alone is not enough: under real network loss a
    /// chunk can still be in flight or retransmitting on a slow lane while a different,
    /// faster lane's local buffer is already empty. Since <see cref="TransferSession"/>
    /// sends the terminating Done message through the normal striped path immediately
    /// after this returns, and work-stealing can put Done on any lane, an early return
    /// here lets Done overtake a straggler chunk. So after the local drain, this sends
    /// one FlushMarker down every lane — pinned to that lane, bypassing the pump — and
    /// waits for the peer to echo a matching FlushAck on the same lane. SCTP delivers
    /// in order within an association, so a lane's ack proves every chunk previously
    /// sent on that lane has actually arrived.
    /// </remarks>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfPumpFaulted();

            var queued = _outbound.Reader.Count;
            var inFlight = Volatile.Read(ref _inFlight);
            var buffered = _lanes.Sum(lane => (double)lane.BufferedAmount);

            if (queued == 0 && inFlight == 0 && buffered == 0) break;

            await Task.Delay(DrainPollInterval, ct);
        }

        var acks = new TaskCompletionSource<bool>[_lanes.Length];
        for (var i = 0; i < acks.Length; i++)
        {
            acks[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Written before any marker is sent, so the merge loop can never observe an
        // ack for a marker whose TCS isn't in place yet.
        Volatile.Write(ref _pendingFlushAcks, acks);

        try
        {
            for (var i = 0; i < _lanes.Length; i++)
            {
                ThrowIfPumpFaulted();
                await _lanes[i].SendAsync(BuildFlushMarker(i), ct);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(FlushBarrierTimeout);

            try
            {
                await Task.WhenAll(acks.Select(ack => ack.Task.WaitAsync(timeoutCts.Token)));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TransferException(
                    $"Flush barrier timed out after {FlushBarrierTimeout.TotalSeconds}s waiting for " +
                    "the peer to acknowledge every lane.");
            }
        }
        finally
        {
            Volatile.Write(ref _pendingFlushAcks, null);
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

        // Unsubscribe from the peer disconnected event to prevent memory leaks
        _signaling.PeerDisconnected -= OnPeerDisconnected;
        
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

        // ponytail: _receiveCts is intentionally never disposed. Disposing it here
        // raced ReceiveAsync (line ~171, reads _receiveCts.Token) and
        // OnPeerDisconnected (below, calls _receiveCts.Cancel()) — both still
        // reachable during/after teardown — throwing ObjectDisposedException where
        // callers expect OperationCanceledException. It carries no unmanaged
        // resources (never linked to a timer or another token), so leaving it for
        // the GC is safe.
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

    /// <summary>Builds a <see cref="TransferMessageType.FlushMarker"/> message: type byte + 4-byte LE lane index.</summary>
    internal static byte[] BuildFlushMarker(int laneIndex) =>
        BuildLaneTaggedMessage(TransferMessageType.FlushMarker, laneIndex);

    /// <summary>Builds a <see cref="TransferMessageType.FlushAck"/> message: type byte + 4-byte LE lane index.</summary>
    internal static byte[] BuildFlushAck(int laneIndex) =>
        BuildLaneTaggedMessage(TransferMessageType.FlushAck, laneIndex);

    private static byte[] BuildLaneTaggedMessage(TransferMessageType type, int laneIndex)
    {
        var message = new byte[5];
        message[0] = (byte)type;
        BitConverter.TryWriteBytes(message.AsSpan(1), laneIndex);
        return message;
    }

    /// <summary>
    /// True if <paramref name="message"/> is a 5-byte <see cref="TransferMessageType.FlushMarker"/>
    /// or <see cref="TransferMessageType.FlushAck"/> message, with its lane index decoded.
    /// </summary>
    internal static bool TryReadFlushProtocolMessage(
        byte[] message, out TransferMessageType type, out int laneIndex)
    {
        if (message.Length == 5 &&
            (message[0] == (byte)TransferMessageType.FlushMarker ||
             message[0] == (byte)TransferMessageType.FlushAck))
        {
            type = (TransferMessageType)message[0];
            laneIndex = BitConverter.ToInt32(message, 1);
            return true;
        }

        type = default;
        laneIndex = default;
        return false;
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

                if (TryReadFlushProtocolMessage(message, out var type, out var laneIndex))
                {
                    if (type == TransferMessageType.FlushMarker)
                    {
                        // Echo back on the same physical lane the marker arrived on —
                        // the marker's own laneIndex is only used by the sender to
                        // route the resulting ack to the right TaskCompletionSource.
                        await lane.SendAsync(BuildFlushAck(laneIndex), _lifetime.Token);
                    }
                    else
                    {
                        Volatile.Read(ref _pendingFlushAcks)?[laneIndex].TrySetResult(true);
                    }

                    continue;
                }

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
    
    /// <summary>
    /// Handles peer disconnection by cancelling any active receive operations.
    /// </summary>
    private void OnPeerDisconnected(object? sender, EventArgs e)
    {
        Log($"[{_role}] Peer disconnected, cancelling active receive");
        try
        {
            _receiveCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Unsubscribed in DisposeAsync before teardown, but a dispatch already
            // in flight can still land here — harmless once dispose has started.
        }
    }
}
