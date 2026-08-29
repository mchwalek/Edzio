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
public class SendViewModel : BaseViewModel, ITransferProgress
{
    private readonly ISignalingClient _signaling;
    private readonly SignalingConnectionManager _connectionManager;
    private readonly TransferRepository _repo;
    private readonly SettingsViewModel _settings;
    private readonly ILogger<WebRtcChannel> _webRtcLogger;

    public ObservableCollection<string> SelectedPaths { get; } = new();

    private string _pairingCode = "";
    private string _statusMessage = "Select files or a folder to send.";
    private double _progressValue = 0;
    private bool _showProgress = false;
    private bool _isComplete = false;
    private string _speedText = "—";
    private string _remainingText = "calculating…";
    private string _transferredText = "";
    private bool _isConnectionReady;
    private bool _isWaitingForConnection = true;

    /// <summary>
    /// The receiver's pairing code. Uppercased live as it is set, so the
    /// bound Entry always displays uppercase regardless of how the user typed it.
    /// </summary>
    public string PairingCode
    {
        get => _pairingCode;
        set => SetProperty(ref _pairingCode, value?.ToUpperInvariant() ?? string.Empty);
    }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public bool ShowProgress { get => _showProgress; private set => SetProperty(ref _showProgress, value); }
    public bool IsComplete { get => _isComplete; private set => SetProperty(ref _isComplete, value); }

    public bool IsConnectionReady
    {
        get => _isConnectionReady;
        private set { SetProperty(ref _isConnectionReady, value); ((Command)SendCommand).ChangeCanExecute(); }
    }

    public bool IsWaitingForConnection { get => _isWaitingForConnection; private set => SetProperty(ref _isWaitingForConnection, value); }

    /// <summary>Current smoothed transfer rate, formatted for display (e.g. "2.4 MB/s").</summary>
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }

    /// <summary>Estimated time remaining, formatted for display (e.g. "1:32 remaining").</summary>
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }

    /// <summary>Bytes sent so far vs. total, formatted for display (e.g. "12.3 MB / 45.0 MB").</summary>
    public string TransferredText { get => _transferredText; private set => SetProperty(ref _transferredText, value); }

    // Query properties for local peer navigation
    public string? LocalPeerIp { set { /* store for future local peer support */ } }
    public string? LocalPeerPort { set { /* store for future local peer support */ } }
    public string? LocalPeerName { set { /* store for future local peer support */ } }

    public ICommand PickFilesCommand { get; }
    public ICommand SendCommand { get; }

    public SendViewModel(ISignalingClient signaling, SignalingConnectionManager connectionManager,
        TransferRepository repo, SettingsViewModel settings, ILogger<WebRtcChannel> webRtcLogger)
    {
        _signaling = signaling;
        _connectionManager = connectionManager;
        _repo = repo;
        _settings = settings;
        _webRtcLogger = webRtcLogger;
        Title = "Send";
        PickFilesCommand = new Command(async () => await PickFilesAsync());
        SendCommand = new Command(async () => await SendAsync(), () => SelectedPaths.Count > 0 && !IsBusy && IsConnectionReady);

        ApplyConnectionState(connectionManager.State);
        connectionManager.StateChanged += (_, state) =>
            MainThread.BeginInvokeOnMainThread(() => ApplyConnectionState(state));
    }

    private void ApplyConnectionState(ConnectionManagerState state)
    {
        IsConnectionReady = state == ConnectionManagerState.Connected;
        IsWaitingForConnection = !IsConnectionReady;
    }

    private async Task PickFilesAsync()
    {
        var result = await FilePicker.PickMultipleAsync();
        if (result is null) return;
        SelectedPaths.Clear();
        AddPaths(result.Select(f => f.FullPath));
    }

    /// <summary>
    /// Adds new file paths to the current selection, skipping duplicates
    /// (case-insensitive). Shared by the file picker and drag-and-drop so
    /// there is one code path for "files were added to the selection."
    /// </summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (SelectedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            SelectedPaths.Add(path);
            added = true;
        }

        if (added)
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

            StatusMessage = "Waiting for connection...";
            EdzioLog.Info("SendVM", "Waiting for signaling connection...");
            await _connectionManager.WaitForConnectedAsync();
            EdzioLog.Info("SendVM", "Signaling connection ready");

            // PairingCode is already uppercased live by the property setter above;
            // ToUpperInvariant() here is a harmless defensive no-op in case the
            // value is ever set some other way that bypasses the setter's normalization.
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

            EdzioLog.Info("SendVM", "Negotiating transfer channel (LAN-direct first, WebRTC fallback)...");
            await using var channel = await TransferChannelNegotiator.ConnectAsSenderAsync(
                rtcConfig, _signaling, _webRtcLogger);
            EdzioLog.Info("SendVM", $"Channel established: {channel.GetType().Name}");

            var sourceRoot = Path.GetDirectoryName(SelectedPaths[0]) ?? SelectedPaths[0];
            var rateTracker = new TransferRateTracker();
            var progress = new Progress<TransferProgress>(p =>
            {
                ProgressValue = p.Percentage / 100.0;
                StatusMessage = $"Sending... {p.Percentage:F0}%";

                var snapshot = rateTracker.Sample(p.BytesSent, p.TotalBytes, DateTimeOffset.UtcNow);
                SpeedText = ByteFormatter.FormatRate(snapshot.BytesPerSecond);
                RemainingText = snapshot.EtaSeconds is { } eta
                    ? $"{ByteFormatter.FormatDuration(eta)} remaining"
                    : "calculating…";
                TransferredText = $"{ByteFormatter.Format(p.BytesSent)} / {ByteFormatter.Format(p.TotalBytes)}";
            });
            var throttledProgress = new ThrottledProgress<TransferProgress>(
                progress, TimeSpan.FromMilliseconds(500), p => p.BytesSent >= p.TotalBytes);

            await TransferSession.SendAsync(sourceRoot, manifest, channel, _repo, throttledProgress);
            IsComplete = true;
            ShowProgress = false;
            StatusMessage = "Sent successfully!";
        }
        catch (Exception ex)
        {
            EdzioLog.Error("SendVM", "Send failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
