using Edzio.Core.Transfer;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

public class InstantSendHandshakeTests
{
    private sealed class LoopbackChannel
    {
        private readonly System.Threading.Channels.Channel<byte[]> _toReceiver = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly System.Threading.Channels.Channel<byte[]> _toSender = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public ITransferChannel AsSender => new Endpoint(_toReceiver.Writer, _toSender.Reader);
        public ITransferChannel AsReceiver => new Endpoint(_toSender.Writer, _toReceiver.Reader);

        private sealed class Endpoint : ITransferChannel
        {
            private readonly System.Threading.Channels.ChannelWriter<byte[]> _writer;
            private readonly System.Threading.Channels.ChannelReader<byte[]> _reader;
            public Endpoint(System.Threading.Channels.ChannelWriter<byte[]> writer, System.Threading.Channels.ChannelReader<byte[]> reader)
            { _writer = writer; _reader = reader; }
            public Task SendAsync(byte[] data, CancellationToken ct = default) { _writer.TryWrite(data); return Task.CompletedTask; }
            public async Task<byte[]> ReceiveAsync(CancellationToken ct = default) => await _reader.ReadAsync(ct);
            public Task WaitForOpenAsync(CancellationToken ct = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task SendOfferAsync_ThenReceiveOfferAsync_RoundTrips()
    {
        var loopback = new LoopbackChannel();
        var offer = new TransferOffer("Alice's PC", new[] { new TransferOfferFile("photo.jpg", 12345) });

        await InstantSendHandshake.SendOfferAsync(loopback.AsSender, offer, CancellationToken.None);
        var received = await InstantSendHandshake.ReceiveOfferAsync(loopback.AsReceiver, CancellationToken.None);

        received.SenderName.Should().Be("Alice's PC");
        received.Files.Should().ContainSingle().Which.Should().Be(new TransferOfferFile("photo.jpg", 12345));
    }

    [Fact]
    public async Task SendResponseAsync_Accept_ThenReceiveResponseAsync_ReturnsTrue()
    {
        var loopback = new LoopbackChannel();
        await InstantSendHandshake.SendResponseAsync(loopback.AsSender, accept: true, CancellationToken.None);
        var accepted = await InstantSendHandshake.ReceiveResponseAsync(loopback.AsReceiver, CancellationToken.None);
        accepted.Should().BeTrue();
    }

    [Fact]
    public async Task SendResponseAsync_Decline_ThenReceiveResponseAsync_ReturnsFalse()
    {
        var loopback = new LoopbackChannel();
        await InstantSendHandshake.SendResponseAsync(loopback.AsSender, accept: false, CancellationToken.None);
        var accepted = await InstantSendHandshake.ReceiveResponseAsync(loopback.AsReceiver, CancellationToken.None);
        accepted.Should().BeFalse();
    }

    [Fact]
    public async Task ReceiveOfferAsync_WrongMessageType_ThrowsTransferException()
    {
        var loopback = new LoopbackChannel();
        await loopback.AsSender.SendAsync(new byte[] { 0xFF });

        var act = async () => await InstantSendHandshake.ReceiveOfferAsync(loopback.AsReceiver, CancellationToken.None);

        await act.Should().ThrowAsync<TransferException>();
    }

    [Fact]
    public async Task ReceiveOfferAsync_TooManyFiles_ThrowsTransferException()
    {
        var loopback = new LoopbackChannel();
        var manyFiles = Enumerable.Range(0, 10_001).Select(i => new TransferOfferFile($"file{i}.bin", 1)).ToArray();
        var offer = new TransferOffer("Bob", manyFiles);
        await InstantSendHandshake.SendOfferAsync(loopback.AsSender, offer, CancellationToken.None);

        var act = async () => await InstantSendHandshake.ReceiveOfferAsync(loopback.AsReceiver, CancellationToken.None);

        await act.Should().ThrowAsync<TransferException>();
    }
}
