using Edzio.Core.Discovery;
using Edzio.Core.Persistence;
using Edzio.Core.Transfer;
using Edzio.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Edzio.Desktop.Services;

/// <summary>
/// Bridges the always-on <see cref="InstantReceiveService"/> to the UI: shows an Accept/Decline prompt
/// for incoming offers, and runs the accepted transfer into the configured download location.
/// </summary>
public sealed class IncomingTransferCoordinator
{
    private readonly InstantReceiveService _instantReceiveService;
    private readonly MdnsDiscovery _mdnsDiscovery;
    private readonly SettingsViewModel _settings;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Creates the coordinator. Resolved from DI as a singleton.</summary>
    public IncomingTransferCoordinator(
        InstantReceiveService instantReceiveService,
        MdnsDiscovery mdnsDiscovery,
        SettingsViewModel settings,
        IServiceScopeFactory scopeFactory)
    {
        _instantReceiveService = instantReceiveService;
        _mdnsDiscovery = mdnsDiscovery;
        _settings = settings;
        _scopeFactory = scopeFactory;
    }

    /// <summary>Starts the LAN listener, publishes its endpoint via mDNS, and begins accepting instant-send offers. Call once at app launch.</summary>
    public async void Start()
    {
        _instantReceiveService.IncomingOffer += OnIncomingOffer;
        _instantReceiveService.Start(log: msg => EdzioLog.Info("InstantReceive", msg));

        var ad = _instantReceiveService.Advertisement!;
        _mdnsDiscovery.SetAdvertisement(ad.Port, ad.CertSha256Hex, ad.TokenBase64);
        await _mdnsDiscovery.StartAsync();
    }

    private async void OnIncomingOffer(object? sender, IncomingOfferEventArgs e)
    {
        var fileList = string.Join(", ", e.Offer.Files.Select(f => f.Name));
        var message = e.Offer.Files.Count == 0
            ? $"{e.Offer.SenderName} wants to send you files."
            : $"{e.Offer.SenderName} wants to send: {fileList}";

        var accepted = await MainThread.InvokeOnMainThreadAsync(() =>
            Shell.Current.CurrentPage.DisplayAlert("Incoming transfer", message, "Accept", "Decline"));
        e.Decide(accepted);
        if (!accepted) return;

        _ = ReceiveAcceptedTransferAsync(e);
    }

    private async Task ReceiveAcceptedTransferAsync(IncomingOfferEventArgs e)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<TransferRepository>();
            await TransferSession.ReceiveAsync(_settings.DownloadLocation, e.Offer.SenderName, e.Channel, repo);
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.CurrentPage.DisplayAlert("Transfer complete", $"Received files from {e.Offer.SenderName}.", "OK"));
        }
        catch (Exception ex)
        {
            EdzioLog.Error("InstantReceive", "Receive failed", ex);
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.CurrentPage.DisplayAlert("Transfer failed", ex.Message, "OK"));
        }
        finally
        {
            await e.Channel.DisposeAsync();
            _instantReceiveService.NotifyTransferFinished();
        }
    }
}
