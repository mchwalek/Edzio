using Edzio.Core.Signaling;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Signaling;

public class SignalingClientTests
{
    [Fact]
    public async Task ConnectAsync_WhenServerUnreachable_TransitionsThroughConnectingToDisconnected()
    {
        var client = new SignalingClient();
        var states = new List<SignalingConnectionState>();
        client.ConnectionStateChanged += (_, s) => states.Add(s);

        // Nothing listens on TCP port 1 on loopback, so this fails fast (connection
        // refused) with no real network or server dependency.
        Func<Task> act = () => client.ConnectAsync("http://127.0.0.1:1");

        await act.Should().ThrowAsync<Exception>();
        states.Should().Equal(SignalingConnectionState.Connecting, SignalingConnectionState.Disconnected);
        client.ConnectionState.Should().Be(SignalingConnectionState.Disconnected);
    }
}
