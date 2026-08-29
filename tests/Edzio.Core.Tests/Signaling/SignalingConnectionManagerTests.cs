using Edzio.Core.Signaling;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Edzio.Core.Tests.Signaling;

public class SignalingConnectionManagerTests
{
    [Fact]
    public async Task Start_ConnectsImmediately()
    {
        var client = new FakeSignalingClient();
        var manager = new SignalingConnectionManager(client);

        manager.Start("http://localhost");
        await WaitUntilAsync(() => manager.State == ConnectionManagerState.Connected);

        client.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task Start_WhenConnectFails_TransitionsToFailedAndRetriesAfterInterval()
    {
        var client = new FakeSignalingClient { ThrowOnConnect = true };
        var time = new FakeTimeProvider();
        var manager = new SignalingConnectionManager(client, time, TimeSpan.FromSeconds(10));

        manager.Start("http://localhost");
        await WaitUntilAsync(() => manager.State == ConnectionManagerState.Failed);

        client.ThrowOnConnect = false;
        time.Advance(TimeSpan.FromSeconds(10));

        await WaitUntilAsync(() => manager.State == ConnectionManagerState.Connected);
        client.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task RetryNow_SkipsTheRemainingDelay()
    {
        var client = new FakeSignalingClient { ThrowOnConnect = true };
        var time = new FakeTimeProvider();
        var manager = new SignalingConnectionManager(client, time, TimeSpan.FromMinutes(10));

        manager.Start("http://localhost");
        await WaitUntilAsync(() => manager.State == ConnectionManagerState.Failed);

        client.ThrowOnConnect = false;
        manager.RetryNow();

        await WaitUntilAsync(() => manager.State == ConnectionManagerState.Connected);
        client.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateUrl_WhenUrlActuallyChanges_Reconnects()
    {
        var client = new FakeSignalingClient();
        var manager = new SignalingConnectionManager(client);
        manager.Start("http://old");
        await WaitUntilAsync(() => manager.State == ConnectionManagerState.Connected);

        manager.UpdateUrl("http://new");

        await WaitUntilAsync(() => manager.State == ConnectionManagerState.Connected);
        client.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task ClientDroppingAfterExhaustingItsOwnReconnect_StartsANewConnectAttempt()
    {
        var client = new FakeSignalingClient();
        var time = new FakeTimeProvider();
        var manager = new SignalingConnectionManager(client, time, TimeSpan.FromSeconds(10));
        manager.Start("http://localhost");
        await WaitUntilAsync(() => manager.State == ConnectionManagerState.Connected);

        // SignalR's own automatic reconnect gave up.
        client.SetConnectionState(SignalingConnectionState.Disconnected);

        await WaitUntilAsync(() => manager.State == ConnectionManagerState.Connected);
        client.Connected.Should().BeTrue("the manager should have started a new connect attempt");
    }

    [Fact]
    public async Task WaitForConnectedAsync_DelegatesToTheClient()
    {
        var client = new FakeSignalingClient();
        var manager = new SignalingConnectionManager(client);

        var wait = manager.WaitForConnectedAsync();
        wait.IsCompleted.Should().BeFalse();

        manager.Start("http://localhost");

        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        wait.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
