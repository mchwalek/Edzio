namespace Edzio.Core.Signaling;

/// <summary>
/// UI-facing connection state. Distinguishes "actively retrying after giving up"
/// (<see cref="Failed"/>) from the SignalR-internal <see cref="SignalingConnectionState.Reconnecting"/>.
/// </summary>
public enum ConnectionManagerState
{
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>
/// Connects an <see cref="ISignalingClient"/> eagerly and keeps retrying on a timer
/// whenever the connection is down — both for the initial connect and for drops that
/// exhaust SignalR's own automatic reconnect. Framework-agnostic so it can be unit
/// tested without a MAUI host.
/// </summary>
public sealed class SignalingConnectionManager : IAsyncDisposable
{
    private readonly ISignalingClient _client;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryInterval;
    private readonly SemaphoreSlim _retrySignal = new(0);
    private readonly object _gate = new();

    private string _url = "";
    private CancellationTokenSource _loopCts = new();
    private Task _loopTask = Task.CompletedTask;
    private ConnectionManagerState _state = ConnectionManagerState.Connecting;

    /// <summary>The current UI-facing connection state.</summary>
    public ConnectionManagerState State { get { lock (_gate) return _state; } }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event EventHandler<ConnectionManagerState>? StateChanged;

    public SignalingConnectionManager(ISignalingClient client, TimeProvider? timeProvider = null, TimeSpan? retryInterval = null)
    {
        _client = client;
        _time = timeProvider ?? TimeProvider.System;
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(10);
        _client.ConnectionStateChanged += OnClientConnectionStateChanged;
    }

    /// <summary>Starts connecting to <paramref name="url"/>. Call once at app launch.</summary>
    public void Start(string url)
    {
        lock (_gate) _url = url;
        RestartLoop();
    }

    /// <summary>Reconnects using a new URL (e.g. the user changed it in Settings). No-op if unchanged.</summary>
    public void UpdateUrl(string url)
    {
        lock (_gate)
        {
            if (_url == url) return;
            _url = url;
        }
        RestartLoop();
    }

    /// <summary>Cancels any pending retry delay so the next attempt happens now.</summary>
    public void RetryNow() => _retrySignal.Release();

    /// <summary>Waits for the underlying client to reach Connected. Never times out on its own.</summary>
    public Task WaitForConnectedAsync(CancellationToken ct = default) => _client.WaitForConnectedAsync(ct);

    private void RestartLoop()
    {
        CancellationTokenSource previousCts;
        string url;
        lock (_gate)
        {
            previousCts = _loopCts;
            _loopCts = new CancellationTokenSource();
            url = _url;
        }
        previousCts.Cancel();
        previousCts.Dispose();

        SetState(ConnectionManagerState.Connecting);

        lock (_gate)
        {
            _loopTask = RunLoopAsync(url, _loopCts.Token);
        }
    }

    private async Task RunLoopAsync(string url, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _client.ConnectAsync(url, ct);
                // Set state directly rather than relying solely on ConnectionStateChanged:
                // if the client was already Connected (e.g. reconnecting to a new URL after
                // ConnectAsync no-ops/resets internally), the tracker may not raise a change
                // event since its state didn't move. Further drops/reconnects are still
                // driven by ConnectionStateChanged.
                SetState(ConnectionManagerState.Connected);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                SetState(ConnectionManagerState.Failed);
                await WaitForRetryOrCancelAsync(ct);
            }
        }
    }

    private async Task WaitForRetryOrCancelAsync(CancellationToken ct)
    {
        var delayTask = Task.Delay(_retryInterval, _time, ct);
        var retryTask = _retrySignal.WaitAsync(ct);
        try
        {
            await Task.WhenAny(delayTask, retryTask);
        }
        catch (OperationCanceledException)
        {
            // The loop's while-condition exits on the next check.
        }
    }

    private void OnClientConnectionStateChanged(object? sender, SignalingConnectionState state)
    {
        switch (state)
        {
            case SignalingConnectionState.Connected:
                SetState(ConnectionManagerState.Connected);
                break;
            case SignalingConnectionState.Reconnecting:
                SetState(ConnectionManagerState.Reconnecting);
                break;
            case SignalingConnectionState.Disconnected:
                bool idle;
                lock (_gate) idle = _loopTask.IsCompleted;
                // Only restart if our own connect/retry loop isn't already handling this
                // (it already reacts to ConnectAsync failures in its catch block). This
                // branch is for a genuine post-success drop — SignalR gave up reconnecting.
                if (idle) RestartLoop();
                break;
        }
    }

    private void SetState(ConnectionManagerState newState)
    {
        lock (_gate)
        {
            if (_state == newState) return;
            _state = newState;
        }
        StateChanged?.Invoke(this, newState);
    }

    public async ValueTask DisposeAsync()
    {
        _client.ConnectionStateChanged -= OnClientConnectionStateChanged;
        Task loopTask;
        lock (_gate)
        {
            _loopCts.Cancel();
            loopTask = _loopTask;
        }
        try { await loopTask; } catch { /* the loop observes its own cancellation */ }
        _loopCts.Dispose();
        _retrySignal.Dispose();
    }
}
