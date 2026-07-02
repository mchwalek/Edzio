using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Signaling;

public class FakeSignalingClientTests
{
    [Fact]
    public async Task ConnectAsync_SetsConnected()
    {
        var fake = new FakeSignalingClient();
        await fake.ConnectAsync("http://localhost");
        fake.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAsReceiverAsync_ReturnsGeneratedCode()
    {
        var fake = new FakeSignalingClient { GeneratedCode = "TEST42" };
        var code = await fake.RegisterAsReceiverAsync();
        code.Should().Be("TEST42");
    }

    [Fact]
    public async Task SendOfferAsync_RecordsOffer()
    {
        var fake = new FakeSignalingClient();
        await fake.SendOfferAsync("sdp-offer");
        fake.SentOffers.Should().ContainSingle().Which.Should().Be("sdp-offer");
    }

    [Fact]
    public void SimulateOfferReceived_FiresEvent()
    {
        var fake = new FakeSignalingClient();
        string? received = null;
        fake.OfferReceived += (_, sdp) => received = sdp;
        fake.SimulateOfferReceived("my-offer");
        received.Should().Be("my-offer");
    }

    [Fact]
    public void SimulatePeerJoined_FiresEvent()
    {
        var fake = new FakeSignalingClient();
        bool fired = false;
        fake.PeerJoined += (_, _) => fired = true;
        fake.SimulatePeerJoined();
        fired.Should().BeTrue();
    }
}
