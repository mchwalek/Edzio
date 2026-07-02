using Edzio.SignalingServer;
using FluentAssertions;
using Xunit;

namespace Edzio.SignalingServer.Tests;

public class ServerPortResolverTests
{
    [Fact]
    public void Resolve_WithNullValue_ReturnsDefaultPort()
    {
        var port = ServerPortResolver.Resolve(null);

        port.Should().Be(ServerPortResolver.DefaultPort);
    }

    [Fact]
    public void Resolve_WithEmptyValue_ReturnsDefaultPort()
    {
        var port = ServerPortResolver.Resolve(string.Empty);

        port.Should().Be(ServerPortResolver.DefaultPort);
    }

    [Fact]
    public void Resolve_WithNonNumericValue_ReturnsDefaultPort()
    {
        var port = ServerPortResolver.Resolve("not-a-number");

        port.Should().Be(ServerPortResolver.DefaultPort);
    }

    [Fact]
    public void Resolve_WithZero_ReturnsDefaultPort()
    {
        var port = ServerPortResolver.Resolve("0");

        port.Should().Be(ServerPortResolver.DefaultPort);
    }

    [Fact]
    public void Resolve_WithNegativeValue_ReturnsDefaultPort()
    {
        var port = ServerPortResolver.Resolve("-5");

        port.Should().Be(ServerPortResolver.DefaultPort);
    }

    [Fact]
    public void Resolve_WithValidPort_ReturnsParsedPort()
    {
        var port = ServerPortResolver.Resolve("3000");

        port.Should().Be(3000);
    }
}
