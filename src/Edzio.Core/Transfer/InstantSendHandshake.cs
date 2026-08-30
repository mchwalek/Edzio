using System.Text.Json;

namespace Edzio.Core.Transfer;

/// <summary>Sends and receives the pre-transfer Offer/Response handshake shared by instant-send sender and receiver.</summary>
public static class InstantSendHandshake
{
    /// <summary>Maximum number of files an <see cref="TransferOffer"/> may list before it is rejected as malformed/malicious.</summary>
    private const int MaxOfferFiles = 10_000;

    /// <summary>Sends a <see cref="TransferOffer"/> as an <see cref="TransferMessageType.Offer"/> message.</summary>
    public static async Task SendOfferAsync(ITransferChannel channel, TransferOffer offer, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(offer);
        var message = new byte[1 + json.Length];
        message[0] = (byte)TransferMessageType.Offer;
        json.CopyTo(message, 1);
        await channel.SendAsync(message, ct);
    }

    /// <summary>Receives and validates an <see cref="TransferMessageType.Offer"/> message.</summary>
    /// <exception cref="TransferException">The message is not an Offer, is malformed, or exceeds <see cref="MaxOfferFiles"/> files.</exception>
    public static async Task<TransferOffer> ReceiveOfferAsync(ITransferChannel channel, CancellationToken ct = default)
    {
        var message = await channel.ReceiveAsync(ct);
        if (message.Length < 1 || message[0] != (byte)TransferMessageType.Offer)
            throw new TransferException("Expected an Offer message.");

        TransferOffer? offer;
        try
        {
            offer = JsonSerializer.Deserialize<TransferOffer>(message.AsSpan(1));
        }
        catch (JsonException ex)
        {
            throw new TransferException("Malformed transfer offer.", ex);
        }
        if (offer is null) throw new TransferException("Malformed transfer offer.");
        if (offer.Files.Count > MaxOfferFiles) throw new TransferException($"Transfer offer exceeds the maximum of {MaxOfferFiles} files.");
        return offer;
    }

    /// <summary>Sends an accept/decline <see cref="TransferMessageType.OfferResponse"/> message.</summary>
    public static async Task SendResponseAsync(ITransferChannel channel, bool accept, CancellationToken ct = default)
    {
        var message = new byte[] { (byte)TransferMessageType.OfferResponse, (byte)(accept ? 1 : 0) };
        await channel.SendAsync(message, ct);
    }

    /// <summary>Receives and validates an <see cref="TransferMessageType.OfferResponse"/> message, returning whether the offer was accepted.</summary>
    /// <exception cref="TransferException">The message is not an OfferResponse.</exception>
    public static async Task<bool> ReceiveResponseAsync(ITransferChannel channel, CancellationToken ct = default)
    {
        var message = await channel.ReceiveAsync(ct);
        if (message.Length < 2 || message[0] != (byte)TransferMessageType.OfferResponse)
            throw new TransferException("Expected an OfferResponse message.");
        return message[1] == 1;
    }
}
