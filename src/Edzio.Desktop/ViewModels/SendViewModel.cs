using System.Collections.ObjectModel;
using System.Windows.Input;
using Edzio.Core.Persistence;
using Edzio.Core.Signaling;
using Edzio.Core.Transfer;
using Edzio.Core.WebRtc;
using Edzio.Desktop.Services;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Edzio.Desktop.ViewModels;

[QueryProperty(nameof(LocalPeerIp), "localPeerIp")]
[QueryProperty(nameof(LocalPeerPort), "localPeerPort")]
[QueryProperty(nameof(LocalPeerName), "localPeerName")]
public class SendViewModel : BaseViewModel
{
    private readonly ISignalingClient _signaling;
    private readonly TransferRepository _repo;
    private readonly SettingsViewModel _settings;
    private readonly ILogger<WebRtcChannel> _webRtcLogger;

    public ObservableCollection<string> SelectedPaths { get; } = new();

    private string _pairingCode = "";
    private string _statusMessage = "Select files or a folder to send.";
    private double _progressValue = 0;
    private bool _showProgress = false;
    private bool _isComplete = false;

    public string PairingCode { get => _pairingCode; set => SetProperty(ref _pairingCode, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public bool ShowProgress { get => _showProgress; private set => SetProperty(ref _showProgress, value); }
    public bool IsComplete { get => _isComplete; private set => SetProperty(ref _isComplete, value); }

    // Query properties for local peer navigation
    public string? LocalPeerIp { set { /* store for future local peer support */ } }
    public string? LocalPeerPort { set { /* store for future local peer support */ } }
    public string? LocalPeerName { set { /* store for future local peer support */ } }

    public ICommand PickFilesCommand { get; }
    public ICommand SendCommand { get; }

    public SendViewModel(ISignalingClient signaling, TransferRepository repo,
        SettingsViewModel settings, ILogger<WebRtcChannel> webRtcLogger)
    {
        _signaling = signaling;
        _repo = repo;
        _settings = settings;
        _webRtcLogger = webRtcLogger;
        Title = "Send";
        PickFilesCommand = new Command(async () => await PickFilesAsync());
        SendCommand = new Command(async () => await SendAsync(), () => SelectedPaths.Count > 0 && !IsBusy);
    }

    private async Task PickFilesAsync()
    {
        var result = await FilePicker.PickMultipleAsync();
        if (result is null) return;
        SelectedPaths.Clear();
        foreach (var f in result) SelectedPaths.Add(f.FullPath);
        ((Command)SendCommand).ChangeCanExecute();
    }

    private async Task SendAsync()
    {
        try
        {
            IsBusy = true;
            ShowProgress = true;
            StatusMessage = "Building transfer manifest...";

            var sessionId = Guid.NewGuid().ToString();
            EdzioLog.Info("SendVM", $"Building manifest for {SelectedPaths.Count} path(s)");
            var manifest = await TransferManifestBuilder.BuildAsync(sessionId, SelectedPaths);
            EdzioLog.Info("SendVM", $"Manifest built: {manifest.Files.Count} file(s), {manifest.TotalBytes:N0} bytes");

            StatusMessage = "Connecting to signaling server...";
            EdzioLog.Info("SendVM", $"Connecting to signaling server: {_settings.SignalingServerUrl}");
            await _signaling.ConnectAsync(_settings.SignalingServerUrl);
            EdzioLog.Info("SendVM", "Connected to signaling server");

            var code = PairingCode.Trim().ToUpperInvariant();
            EdzioLog.Info("SendVM", $"Joining as sender with code: {code}");
            var joined = await _signaling.JoinAsSenderAsync(code);
            if (!joined)
            {
                EdzioLog.Warn("SendVM", "JoinAsSender returned false — invalid or expired code");
                StatusMessage = "Invalid or expired code. Please try again.";
                IsBusy = false;
                return;
            }
            EdzioLog.Info("SendVM", "JoinAsSender succeeded — receiver is connected");

            StatusMessage = "Establishing direct connection...";
            var rtcConfig = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } }
            };
            if (!string.IsNullOrEmpty(_settings.TurnServerUrl))
                rtcConfig.iceServers.Add(new RTCIceServer
                {
                    urls = _settings.TurnServerUrl,
                    username = _settings.TurnUsername,
                    credential = _settings.TurnCredential
                });

            EdzioLog.Info("SendVM", "Creating WebRtcChannel (Offerer)...");
            await using var channel = new WebRtcChannel(rtcConfig, _signaling, WebRtcRole.Offerer, _webRtcLogger);
            await channel.ConnectAsync();
            EdzioLog.Info("SendVM", "ConnectAsync complete");

            var sourceRoot = Path.GetDirectoryName(SelectedPaths[0]) ?? SelectedPaths[0];
            var progress = new Progress<TransferProgress>(p =>
            {
                ProgressValue = p.Percentage / 100.0;
                StatusMessage = $"Sending... {p.Percentage:F0}%";
            });

            await TransferSession.SendAsync(sourceRoot, manifest, channel, _repo, progress);
            IsComplete = true;
            StatusMessage = "Sent successfully!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
