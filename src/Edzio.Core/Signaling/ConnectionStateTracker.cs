namespace Edzio.Core.Signaling;

/// <summary>
/// The lifecycle of a signaling connection, mirroring the underlying SignalR
/// <c>HubConnection</c> state machine.
/// </summary>
public enum SignalingConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

/// <summary>
/// Tracks a <see cref="SignalingConnectionState"/> and lets callers await the next
/// transition to <see cref="SignalingConnectionState.Connected"/>. Has no dependency
/// on SignalR, so it can be unit-tested directly without a live connection.
/// </summary>
public sealed class ConnectionStateTracker
{
    private readonly object _gate = new();
    private TaskCompletionSource _connectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The current connection state.</summary>
    public SignalingConnectionState State { get; private set; } = SignalingConnectionState.Disconnected;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event EventHandler<SignalingConnectionState>? Changed;

    /// <summary>
    /// Moves to <paramref name="newState"/> and raises <see cref="Changed"/> if the
    /// state actually differs from the current one. Completes any pending
    /// <see cref="WaitForConnectedAsync"/> callers when the new state is Connected.
    /// </summary>
    public void TransitionTo(SignalingConnectionState newState)
    {
        lock (_gate)
        {
            if (State == newState) return;
            State = newState;

            if (newState == SignalingConnectionState.Connected)
            {
                _connectedTcs.TrySetResult();
            }
            else if (_connectedTcs.Task.IsCompleted)
            {
                // A fresh, not-yet-connected wait is needed for the next Connected transition.
                _connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        Changed?.Invoke(this, newState);
    }

    /// <summary>
    /// Completes immediately if already Connected; otherwise completes on the next
    /// transition to Connected. Never polls.
    /// </summary>
    public Task WaitForConnectedAsync(CancellationToken ct = default)
    {
        Task task;
        lock (_gate) task = _connectedTcs.Task;
        return task.WaitAsync(ct);
    }
}
