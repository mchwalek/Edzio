using Edzio.Core.Discovery;
using FluentAssertions;
using Xunit;
namespace Edzio.Core.Tests.Discovery;
public class LocalDiscoveryTests {
    [Fact]
    public void LocalPeer_RecordEquality_Works() {
        var a = new LocalPeer("Alice", new[] { "192.168.1.5" }, 7777);
        var b = new LocalPeer("Alice", new[] { "192.168.1.5" }, 7777);
        a.Should().Be(b);
    }

    [Fact]
    public void LocalPeer_DifferentIp_NotEqual() {
        var a = new LocalPeer("Alice", new[] { "192.168.1.5" }, 7777);
        var b = new LocalPeer("Alice", new[] { "192.168.1.6" }, 7777);
        a.Should().NotBe(b);
    }

    [Fact]
    public void MdnsDiscovery_ImplementsILocalDiscovery() {
        var sut = new MdnsDiscovery();
        sut.Should().BeAssignableTo<ILocalDiscovery>();
    }

    [Fact]
    public void FakeLocalDiscovery_SimulateDiscovery_AddsPeerAndFiresEvent() {
        var fake = new FakeLocalDiscovery();
        IReadOnlyList<LocalPeer>? eventPeers = null;
        fake.PeersChanged += (_, peers) => eventPeers = peers;

        var peer = new LocalPeer("Bob", new[] { "10.0.0.1" }, 7777);
        fake.SimulateDiscovery(peer);

        fake.DiscoveredPeers.Should().ContainSingle().Which.Should().Be(peer);
        eventPeers.Should().ContainSingle().Which.Should().Be(peer);
    }

    [Fact]
    public async Task FakeLocalDiscovery_StartStop_SetsFlags() {
        var fake = new FakeLocalDiscovery();
        await fake.StartAsync();
        await fake.StopAsync();
        fake.Started.Should().BeTrue();
        fake.Stopped.Should().BeTrue();
    }
}
