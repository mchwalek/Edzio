using System.Net;
using Edzio.Core.Lan;
using Edzio.Core.Transfer;
using FluentAssertions;
using Xunit;

namespace Edzio.Core.Tests.Transfer;

public class InstantReceiveServiceTests
{
    private static readonly IReadOnlyList<IPAddress> Loopback = new[] { IPAddress.Loopback };

    [Fact(Timeout = 20000)]
    public async Task Start_PublishesAdvertisement()
    {
        await using var sut = new InstantReceiveService();
        sut.Start();

        sut.Advertisement.Should().NotBeNull();
        sut.Advertisement!.Port.Should().BeGreaterThan(0);
    }

    [Fact(Timeout = 20000)]
    public async Task IncomingOffer_RaisedOnConnectAndOffer_DecideAccept_SendsAcceptResponse()
    {
        await using var sut = new InstantReceiveService(Loopback);
        sut.Start();
        TransferOffer? receivedOffer = null;
        sut.IncomingOffer += (_, e) => { receivedOffer = e.Offer; e.Decide(true); };

        using var senderCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var senderChannel = await LanDirect.TryConnectAsync(sut.Advertisement!, TimeSpan.FromSeconds(5), ct: senderCts.Token);
        senderChannel.Should().NotBeNull();
        await InstantSendHandshake.SendOfferAsync(senderChannel!, new TransferOffer("Sender", new[] { new TransferOfferFile("a.txt", 1) }), senderCts.Token);
        var accepted = await InstantSendHandshake.ReceiveResponseAsync(senderChannel!, senderCts.Token);

        accepted.Should().BeTrue();
        receivedOffer!.SenderName.Should().Be("Sender");
    }

    [Fact(Timeout = 20000)]
    public async Task IncomingOffer_DecideDecline_SendsDeclineResponse()
    {
        await using var sut = new InstantReceiveService(Loopback);
        sut.Start();
        sut.IncomingOffer += (_, e) => e.Decide(false);

        using var senderCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var senderChannel = await LanDirect.TryConnectAsync(sut.Advertisement!, TimeSpan.FromSeconds(5), ct: senderCts.Token);
        await InstantSendHandshake.SendOfferAsync(senderChannel!, new TransferOffer("Sender", Array.Empty<TransferOfferFile>()), senderCts.Token);
        var accepted = await InstantSendHandshake.ReceiveResponseAsync(senderChannel!, senderCts.Token);

        accepted.Should().BeFalse();
    }

    [Fact(Timeout = 20000)]
    public async Task SecondOffer_WhileTransferInProgress_IsAutoDeclinedWithoutRaisingEvent()
    {
        await using var sut = new InstantReceiveService(Loopback);
        sut.Start();
        var offerCount = 0;
        sut.IncomingOffer += (_, e) => { offerCount++; e.Decide(true); }; // first offer accepted, service now "in progress"

        using var firstCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var firstChannel = await LanDirect.TryConnectAsync(sut.Advertisement!, TimeSpan.FromSeconds(5), ct: firstCts.Token);
        await InstantSendHandshake.SendOfferAsync(firstChannel!, new TransferOffer("First", Array.Empty<TransferOfferFile>()), firstCts.Token);
        await InstantSendHandshake.ReceiveResponseAsync(firstChannel!, firstCts.Token);

        using var secondCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var secondChannel = await LanDirect.TryConnectAsync(sut.Advertisement!, TimeSpan.FromSeconds(5), ct: secondCts.Token);
        await InstantSendHandshake.SendOfferAsync(secondChannel!, new TransferOffer("Second", Array.Empty<TransferOfferFile>()), secondCts.Token);
        var secondAccepted = await InstantSendHandshake.ReceiveResponseAsync(secondChannel!, secondCts.Token);

        secondAccepted.Should().BeFalse();
        offerCount.Should().Be(1);
    }

    [Fact(Timeout = 20000)]
    public async Task DisposeAsync_WhilePendingDecision_CompletesPromptlyInsteadOfHanging()
    {
        var sut = new InstantReceiveService(Loopback);
        sut.Start();
        sut.IncomingOffer += (_, _) => { }; // never calls Decide — simulates the app closing mid-prompt

        using var senderCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var senderChannel = await LanDirect.TryConnectAsync(sut.Advertisement!, TimeSpan.FromSeconds(5), ct: senderCts.Token);
        await InstantSendHandshake.SendOfferAsync(senderChannel!, new TransferOffer("Sender", Array.Empty<TransferOfferFile>()), senderCts.Token);

        // Give the accept loop time to receive the offer and block awaiting the (never-arriving) decision.
        await Task.Delay(200);

        Func<Task> dispose = () => sut.DisposeAsync().AsTask();

        await dispose.Should().CompleteWithinAsync(TimeSpan.FromSeconds(5));
    }
}
