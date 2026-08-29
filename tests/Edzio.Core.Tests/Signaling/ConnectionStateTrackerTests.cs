using Edzio.Core.Signaling;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Signaling;

public class ConnectionStateTrackerTests
{
    [Fact]
    public void InitialState_IsDisconnected()
    {
        var tracker = new ConnectionStateTracker();
        tracker.State.Should().Be(SignalingConnectionState.Disconnected);
    }

    [Fact]
    public void TransitionTo_RaisesChangedWithNewState()
    {
        var tracker = new ConnectionStateTracker();
        SignalingConnectionState? seen = null;
        tracker.Changed += (_, s) => seen = s;

        tracker.TransitionTo(SignalingConnectionState.Connecting);

        seen.Should().Be(SignalingConnectionState.Connecting);
        tracker.State.Should().Be(SignalingConnectionState.Connecting);
    }

    [Fact]
    public void TransitionTo_SameState_DoesNotRaiseChanged()
    {
        var tracker = new ConnectionStateTracker();
        tracker.TransitionTo(SignalingConnectionState.Connecting);

        var raiseCount = 0;
        tracker.Changed += (_, _) => raiseCount++;
        tracker.TransitionTo(SignalingConnectionState.Connecting);

        raiseCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitForConnectedAsync_WhenAlreadyConnected_CompletesImmediately()
    {
        var tracker = new ConnectionStateTracker();
        tracker.TransitionTo(SignalingConnectionState.Connected);

        var wait = tracker.WaitForConnectedAsync();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        wait.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForConnectedAsync_WhenPending_CompletesOnNextConnectedTransition()
    {
        var tracker = new ConnectionStateTracker();
        tracker.TransitionTo(SignalingConnectionState.Connecting);

        var wait = tracker.WaitForConnectedAsync();
        wait.IsCompleted.Should().BeFalse();

        tracker.TransitionTo(SignalingConnectionState.Connected);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        wait.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForConnectedAsync_AfterDroppingFromConnected_WaitsAgainForTheNextConnected()
    {
        var tracker = new ConnectionStateTracker();
        tracker.TransitionTo(SignalingConnectionState.Connected);
        tracker.TransitionTo(SignalingConnectionState.Reconnecting);

        var wait = tracker.WaitForConnectedAsync();
        wait.IsCompleted.Should().BeFalse();

        tracker.TransitionTo(SignalingConnectionState.Connected);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        wait.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForConnectedAsync_WhenCancelled_Throws()
    {
        var tracker = new ConnectionStateTracker();
        using var cts = new CancellationTokenSource();
        var wait = tracker.WaitForConnectedAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => wait);
    }
}
