using Edzio.SignalingServer.Services;
using FluentAssertions;
using Xunit;

namespace Edzio.SignalingServer.Tests.Services;

public class PairingCodeServiceTests
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    [Fact]
    public void GenerateCode_ProducesValidLength()
    {
        var sut = new PairingCodeService();

        var code = sut.GenerateCode("conn-1");

        code.Should().HaveLength(6);
    }

    [Fact]
    public void GenerateCode_UsesAllowedAlphabet()
    {
        var sut = new PairingCodeService();

        var code = sut.GenerateCode("conn-1");

        foreach (var ch in code)
        {
            Alphabet.Should().Contain(ch.ToString(),
                because: $"character '{ch}' should be in the allowed alphabet");
        }
    }

    [Fact]
    public void TryJoin_WithValidCode_ReturnsTrue()
    {
        var sut = new PairingCodeService();
        var code = sut.GenerateCode("receiver-1");

        var result = sut.TryJoin(code, "sender-1", out var receiverConnectionId);

        result.Should().BeTrue();
        receiverConnectionId.Should().Be("receiver-1");
    }

    [Fact]
    public void TryJoin_WithInvalidCode_ReturnsFalse()
    {
        var sut = new PairingCodeService();

        var result = sut.TryJoin("XXXXXX", "sender-1", out var receiverConnectionId);

        result.Should().BeFalse();
        receiverConnectionId.Should().BeNull();
    }

    [Fact]
    public void TryJoin_SameCodeTwice_SecondFails()
    {
        var sut = new PairingCodeService();
        var code = sut.GenerateCode("receiver-1");

        var first = sut.TryJoin(code, "sender-1", out _);
        var second = sut.TryJoin(code, "sender-2", out _);

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    public void RemoveConnection_ClearsCode()
    {
        var sut = new PairingCodeService();
        var code = sut.GenerateCode("receiver-1");

        sut.RemoveConnection("receiver-1");

        var result = sut.TryJoin(code, "sender-1", out _);
        result.Should().BeFalse();
    }
}
