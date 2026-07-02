using Edzio.Core.Persistence;
using Edzio.Core.Signaling;
using Edzio.Core.Transfer;
using Edzio.Core.WebRtc;
using Edzio.Desktop.Services;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Edzio.Desktop.ViewModels;

public class ReceiveViewModel : BaseViewModel
{
    private readonly ISignalingClient _signaling;
    private readonly TransferRepository _repo;
    private readonly SettingsViewModel _settings;
    private readonly ILogger<WebRtcChannel> _webRtcLogger;

    private string _pairingCode = "";
    private string _statusMessage = "Starting...";
    private double _progressValue = 0;
    private bool _showCode = false;
    private bool _showProgress = false;
    private bool _isComplete = false;
    private string? _completedPath;

    public string PairingCode { get => _pairingCode; private set => SetProperty(ref _pairingCode, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public bool ShowCode { get => _showCode; private set => SetProperty(ref _showCode, value); }
    public bool ShowProgress { get => _showProgress; private set => SetProperty(ref _showProgress, value); }
    public bool IsComplete { get => _isComplete; private set => SetProperty(ref _isComplete, value); }
    public string? CompletedPath { get => _completedPath; private set => SetProperty(ref _completedPath, value); }

    public ReceiveViewModel(ISignalingClient signaling, TransferRepository repo,
        SettingsViewModel settings, ILogger<WebRtcChannel> webRtcLogger)
    {
        _signaling = signaling;
        _repo = repo;
        _settings = settings;
        _webRtcLogger = webRtcLogger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            EdzioLog.Info("ReceiveVM", $"Connecting to signaling server: {_settings.SignalingServerUrl}");
            await _signaling.ConnectAsync(_settings.SignalingServerUrl, ct);
            EdzioLog.Info("ReceiveVM", "Connected to signaling server");

            PairingCode = await _signaling.RegisterAsReceiverAsync(ct);
            EdzioLog.Info("ReceiveVM", $"Registered as receiver, code: {PairingCode}");
            ShowCode = true;
            StatusMessage = "Share this code with the sender";

            var peerJoinedTcs = new TaskCompletionSource();
            EventHandler handler = (_, _) => peerJoinedTcs.TrySetResult();
            _signaling.PeerJoined += handler;
            try { await peerJoinedTcs.Task.WaitAsync(ct); }
            finally { _signaling.PeerJoined -= handler; }

            EdzioLog.Info("ReceiveVM", "PeerJoined received — sender has connected to signaling server");
            ShowCode = false;
            ShowProgress = true;
            StatusMessage = "Sender connected! Establishing connection...";

            var rtcConfig = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } }
            };

            if (!string.IsNullOrEmpty(_settings.TurnServerUrl))
            {
                rtcConfig.iceServers.Add(new RTCIceServer
                {
                    urls = _settings.TurnServerUrl,
                    username = _settings.TurnUsername,
                    credential = _settings.TurnCredential
                });
            }

            EdzioLog.Info("ReceiveVM", "Creating WebRtcChannel (Answerer)...");
            await using var channel = new WebRtcChannel(rtcConfig, _signaling, WebRtcRole.Answerer, _webRtcLogger);
            await channel.ConnectAsync(ct);
            EdzioLog.Info("ReceiveVM", "ConnectAsync complete — waiting for data channel to open via WaitForOpenAsync...");

            var outputRoot = _settings.DownloadLocation;
            Directory.CreateDirectory(outputRoot);

            var progress = new Progress<TransferProgress>(p =>
            {
                ProgressValue = p.Percentage / 100.0;
                StatusMessage = $"Receiving... {p.Percentage:F0}%";
            });

            await TransferSession.ReceiveAsync(outputRoot, "Sender", channel, _repo, progress, ct);

            CompletedPath = outputRoot;
            IsComplete = true;
            ShowProgress = false;
            StatusMessage = "Transfer complete!";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Transfer cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    public void OpenFolder()
    {
        if (CompletedPath is not null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = CompletedPath,
                UseShellExecute = false
            });
    }
}
