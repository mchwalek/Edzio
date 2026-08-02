using Edzio.Core.Lan;
using Edzio.Core.Signaling;
using Edzio.Core.WebRtc;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Edzio.Core.Transfer;

/// <summary>
/// Chooses the best available <see cref="ITransferChannel"/> between two peers:
/// a LAN-direct TLS connection when both are on the same network (wire speed),
/// falling back to a WebRTC data channel otherwise.
/// </summary>
/// <remarks>
/// Negotiation rides on the existing signaling relay — the receiver advertises
/// its LAN endpoint through the ICE-candidate relay (the deployed signaling
/// server forwards strings blindly, so no server changes are needed), and the
/// sender decides the path: try TCP first, offer WebRTC only if that fails.
/// Ordering assumption: the receiver only advertises after PeerJoined, which
/// the server sends while the sender's JoinAsSender call is completing — so the
/// sender is always subscribed before the advertisement can arrive (verified
/// against SignalingHub.JoinAsSender; the relay round-trip through the server
/// dwarfs the sender's in-process subscribe).
/// </remarks>
public static class TransferChannelNegotiator
{
    /// <summary>How long the sender waits for a LAN endpoint advertisement before going straight to WebRTC.</summary>
    internal static readonly TimeSpan AdvertisementWait = TimeSpan.FromSeconds(2.5);

    /// <summary>How long the sender gives the LAN TCP connect + TLS + auth before falling back to WebRTC.</summary>
    internal static readonly TimeSpan LanConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Sender side: waits briefly for the receiver's LAN endpoint advertisement,
    /// attempts a LAN-direct connection, and falls back to a WebRTC offer if
    /// either step fails. Must be called after JoinAsSender has succeeded.
    /// </summary>
    public static async Task<ITransferChannel> ConnectAsSenderAsync(
        RTCConfiguration rtcConfig,
        ISignalingClient signaling,
        ILogger<WebRtcChannel>? logger = null,
        CancellationToken ct = default)
    {
        var advertisementTcs = new TaskCompletionSource<LanEndpointAdvertisement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<string> handler = (_, json) =>
        {
            if (LanDirect.TryParseAdvertisement(json) is { } ad)
                advertisementTcs.TrySetResult(ad);
        };
        signaling.IceCandidateReceived += handler;

        try
        {
            LanEndpointAdvertisement? ad = null;
            try
            {
                ad = await advertisementTcs.Task.WaitAsync(AdvertisementWait, ct);
            }
            catch (TimeoutException)
            {
                logger?.LogInformation("[Negotiator] No LAN endpoint advertised within {Wait}s — using WebRTC.",
                    AdvertisementWait.TotalSeconds);
            }

            if (ad is not null)
            {
                logger?.LogInformation("[Negotiator] LAN endpoint advertised (port {Port}, {Count} address(es)) — attempting direct TCP...",
                    ad.Port, ad.Addresses.Count);

                var lanChannel = await LanDirect.TryConnectAsync(ad, LanConnectTimeout,
                    log: msg => logger?.LogInformation("{Msg}", msg), ct: ct);
                if (lanChannel is not null)
                {
                    logger?.LogInformation("[Negotiator] LAN-direct channel established.");
                    return lanChannel;
                }

                logger?.LogInformation("[Negotiator] LAN-direct connection failed — falling back to WebRTC.");
            }
        }
        finally
        {
            signaling.IceCandidateReceived -= handler;
        }

        var webRtcChannel = new WebRtcChannel(rtcConfig, signaling, WebRtcRole.Offerer, logger);
        try
        {
            await webRtcChannel.ConnectAsync(ct);
        }
        catch
        {
            await webRtcChannel.DisposeAsync();
            throw;
        }
        return webRtcChannel;
    }

    /// <summary>
    /// Receiver side: starts a LAN listener, advertises it via signaling, and
    /// simultaneously answers WebRTC — whichever path the sender chooses wins;
    /// the loser is torn down. Must be called after PeerJoined.
    /// </summary>
    public static async Task<ITransferChannel> ConnectAsReceiverAsync(
        RTCConfiguration rtcConfig,
        ISignalingClient signaling,
        ILogger<WebRtcChannel>? logger = null,
        CancellationToken ct = default)
    {
        Action<string> log = msg => logger?.LogInformation("{Msg}", msg);
        var listener = LanDirectListener.Start(log: log);
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var webRtcChannel = new WebRtcChannel(rtcConfig, signaling, WebRtcRole.Answerer, logger);

        try
        {
            await signaling.SendIceCandidateAsync(LanDirect.SerializeAdvertisement(listener.Advertisement), ct);

            var tcpTask = listener.AcceptAsync(raceCts.Token, log);
            var webRtcTask = ConnectAndOpenAsync(webRtcChannel, raceCts.Token);

            var winner = await Task.WhenAny(tcpTask, webRtcTask);
            raceCts.Cancel(); // stop the losing path

            if (winner == tcpTask)
            {
                logger?.LogInformation("[Negotiator] Sender connected via LAN-direct TCP.");
                await webRtcTask.ContinueWith(_ => { }, TaskScheduler.Default); // observe cancellation
                await webRtcChannel.DisposeAsync();
                return await tcpTask;
            }

            logger?.LogInformation("[Negotiator] Sender chose WebRTC.");
            await tcpTask.ContinueWith(_ => { }, TaskScheduler.Default); // observe cancellation
            await webRtcTask; // propagate any real WebRTC failure
            return webRtcChannel;
        }
        catch
        {
            await webRtcChannel.DisposeAsync();
            throw;
        }
        finally
        {
            listener.Dispose();
        }
    }

    private static async Task ConnectAndOpenAsync(WebRtcChannel channel, CancellationToken ct)
    {
        await channel.ConnectAsync(ct);
        await channel.WaitForOpenAsync(ct);
    }
}
