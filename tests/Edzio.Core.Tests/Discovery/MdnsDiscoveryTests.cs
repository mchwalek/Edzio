using Makaretu.Dns;
using Edzio.Core.Discovery;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Discovery;

public class MdnsDiscoveryTests
{
    private static Message BuildAnswerMessage(string ip, int port, string displayName, string instanceId, string certSha256, string token)
    {
        var message = new Message();
        message.Answers.Add(new SRVRecord { Port = (ushort)port });
        message.Answers.Add(new TXTRecord
        {
            Strings = new List<string>
            {
                $"displayName={displayName}",
                $"instanceId={instanceId}",
                $"certSha256={certSha256}",
                $"token={token}",
            },
        });
        message.Answers.Add(new ARecord { Address = System.Net.IPAddress.Parse(ip) });
        return message;
    }

    [Fact]
    public void OnServiceInstanceDiscovered_ForeignServiceType_IsIgnored()
    {
        var sut = new MdnsDiscovery("MyPeer");
        var args = new ServiceInstanceDiscoveryEventArgs
        {
            ServiceInstanceName = new DomainName("chromecast._googlecast._tcp.local"),
            Message = BuildAnswerMessage("10.0.0.9", 8009, "SetTopBox", "foreign-id", "", ""),
        };

        sut.OnServiceInstanceDiscovered(null, args);

        sut.DiscoveredPeers.Should().BeEmpty();
    }

    [Fact]
    public void OnServiceInstanceDiscovered_EdzioServiceFromOtherInstance_IsAdded()
    {
        var sut = new MdnsDiscovery("MyPeer");
        var args = new ServiceInstanceDiscoveryEventArgs
        {
            ServiceInstanceName = new DomainName("OtherPeer._edzio._tcp.local"),
            Message = BuildAnswerMessage("192.168.1.50", 7777, "OtherPeer", "other-instance-id", "abc123", "dG9rZW4="),
        };

        sut.OnServiceInstanceDiscovered(null, args);

        sut.DiscoveredPeers.Should().ContainSingle().Which.Should().Be(
            new LocalPeer("OtherPeer", "192.168.1.50", 7777, "other-instance-id", "abc123", "dG9rZW4="));
    }

    [Fact]
    public void OnServiceInstanceDiscovered_OwnInstanceId_IsIgnored()
    {
        var sut = new MdnsDiscovery("MyPeer");
        sut.SetAdvertisement(7777, "self-cert", "self-token");
        var args = new ServiceInstanceDiscoveryEventArgs
        {
            ServiceInstanceName = new DomainName("MyPeer._edzio._tcp.local"),
            Message = BuildAnswerMessage("192.168.1.10", 7777, "MyPeer", sut.InstanceId, "self-cert", "self-token"),
        };

        sut.OnServiceInstanceDiscovered(null, args);

        sut.DiscoveredPeers.Should().BeEmpty();
    }

    [Fact]
    public void OnServiceInstanceShutdown_RemovesPreviouslyDiscoveredPeer()
    {
        var sut = new MdnsDiscovery("MyPeer");
        var name = new DomainName("OtherPeer._edzio._tcp.local");
        var discoveredArgs = new ServiceInstanceDiscoveryEventArgs
        {
            ServiceInstanceName = name,
            Message = BuildAnswerMessage("192.168.1.50", 7777, "OtherPeer", "other-instance-id", "abc123", "dG9rZW4="),
        };
        sut.OnServiceInstanceDiscovered(null, discoveredArgs);
        sut.DiscoveredPeers.Should().ContainSingle();

        var shutdownArgs = new ServiceInstanceShutdownEventArgs { ServiceInstanceName = name, Message = new Message() };
        sut.OnServiceInstanceShutdown(null, shutdownArgs);

        sut.DiscoveredPeers.Should().BeEmpty();
    }

    [Fact]
    public void OnServiceInstanceDiscovered_FiresPeersChangedWithFullSnapshot()
    {
        var sut = new MdnsDiscovery("MyPeer");
        IReadOnlyList<LocalPeer>? received = null;
        sut.PeersChanged += (_, peers) => received = peers;
        var args = new ServiceInstanceDiscoveryEventArgs
        {
            ServiceInstanceName = new DomainName("OtherPeer._edzio._tcp.local"),
            Message = BuildAnswerMessage("192.168.1.50", 7777, "OtherPeer", "other-instance-id", "abc123", "dG9rZW4="),
        };

        sut.OnServiceInstanceDiscovered(null, args);

        received.Should().ContainSingle();
    }
}
