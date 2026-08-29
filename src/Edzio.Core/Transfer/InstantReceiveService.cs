using Edzio.Core.Lan;

namespace Edzio.Core.Transfer;

/// <summary>Event data for an incoming instant-send offer, letting the UI decide whether to accept it.</summary>
public sealed class IncomingOfferEventArgs : EventArgs
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates event data for an incoming offer on an already-authenticated channel.</summary>
    public IncomingOfferEventArgs(TransferOffer offer, ITransferChannel channel)
    {
        Offer = offer;
        Channel = channel;
    }

    /// <summary>The sender name and file list being offered.</summary>
    public TransferOffer Offer { get; }

    /// <summary>The already-authenticated channel this offer arrived on. Ownership transfers to the caller of <see cref="InstantReceiveService.IncomingOffer"/> handlers once accepted.</summary>
    public ITransferChannel Channel { get; }

    /// <summary>Resolves once <see cref="Decide"/> is called.</summary>
    public Task<bool> DecisionTask => _decision.Task;

    /// <summary>Called by the UI handler to accept or decline this offer.</summary>
    public void Decide(bool accept) => _decision.TrySetResult(accept);
}

/// <summary>
/// Always-on LAN listener that accepts incoming instant-send connections, performs the Offer/Response
/// handshake, and hands accepted channels off to a UI-level handler via <see cref="IncomingOffer"/>.
/// Has no Desktop/UI dependencies — the actual <see cref="TransferSession.ReceiveAsync"/> call happens
/// in the consumer of the event, which owns the download-location/repository configuration.
/// </summary>
public sealed class InstantReceiveService : IAsyncDisposable
{
    private readonly IReadOnlyList<System.Net.IPAddress>? _addresses;
    private LanDirectListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private int _transferInProgress;

    /// <summary>Creates the service. <paramref name="addresses"/> defaults to all LAN addresses; tests pass loopback.</summary>
    public InstantReceiveService(IReadOnlyList<System.Net.IPAddress>? addresses = null)
    {
        _addresses = addresses;
    }

    /// <summary>This instance's LAN endpoint, available once <see cref="Start"/> has been called.</summary>
    public LanEndpointAdvertisement? Advertisement => _listener?.Advertisement;

    /// <summary>Raised when a peer connects and offers a transfer. Handlers must call <see cref="IncomingOfferEventArgs.Decide"/>.</summary>
    public event EventHandler<IncomingOfferEventArgs>? IncomingOffer;

    /// <summary>Starts the LAN listener and the background accept loop.</summary>
    public void Start(Action<string>? log = null)
    {
        _listener = LanDirectListener.Start(_addresses, log);
        _cts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(log, _cts.Token));
    }

    /// <summary>Called by the accepted-transfer handler once <see cref="TransferSession.ReceiveAsync"/> completes or fails, so the service can accept the next offer.</summary>
    public void NotifyTransferFinished() => Interlocked.Exchange(ref _transferInProgress, 0);

    private async Task AcceptLoopAsync(Action<string>? log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ITransferChannel? channel = null;
            try
            {
                channel = await _listener!.AcceptAsync(ct, log);
                var offer = await InstantSendHandshake.ReceiveOfferAsync(channel, ct);

                // ponytail: only one inbound transfer at a time; auto-decline concurrent offers. Add a queue if users need parallel receives.
                if (Interlocked.CompareExchange(ref _transferInProgress, 0, 0) == 1)
                {
                    await InstantSendHandshake.SendResponseAsync(channel, accept: false, ct);
                    await channel.DisposeAsync();
                    continue;
                }

                var args = new IncomingOfferEventArgs(offer, channel);
                IncomingOffer?.Invoke(this, args);
                var accepted = await args.DecisionTask;
                await InstantSendHandshake.SendResponseAsync(channel, accepted, ct);

                if (accepted)
                {
                    Interlocked.Exchange(ref _transferInProgress, 1);
                    // Channel ownership now belongs to the IncomingOffer handler; it must dispose it and call NotifyTransferFinished().
                }
                else
                {
                    await channel.DisposeAsync();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log?.Invoke($"InstantReceiveService: dropping bad connection: {ex.Message}");
                if (channel is not null) await channel.DisposeAsync();
            }
        }
    }

    /// <summary>Stops the accept loop and releases the LAN listener.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask; } catch (OperationCanceledException) { }
        }
        _listener?.Dispose();
        _cts?.Dispose();
    }
}
