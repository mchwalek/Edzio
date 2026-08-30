using Edzio.Core.Persistence;
using Edzio.Core.Signaling;
using Edzio.Core.Transfer;
using Edzio.Core.WebRtc;
using Edzio.Desktop.Services;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Edzio.Desktop.ViewModels;

public class ReceiveViewModel : BaseViewModel, ITransferProgress
{
    private readonly ISignalingClient _signaling;
    private readonly SignalingConnectionManager _connectionManager;
    private readonly TransferRepository _repo;
    private readonly SettingsViewModel _settings;
    private readonly ILogger<WebRtcChannel> _webRtcLogger;

    private string _pairingCode = "";
    private string _statusMessage = "Starting...";
    private double _progressValue = 0;
    private bool _showCode = false;
    private bool _showProgress = false;
    private bool _isComplete = false;
    private bool _showInitialStatus = false;
    private bool _isConnectingToServer = true;
    private string? _completedPath;
    private string _speedText = "—";
    private string _remainingText = "calculating…";
    private string _transferredText = "";
    private bool _isInstantMode;

    public string PairingCode { get => _pairingCode; private set => SetProperty(ref _pairingCode, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    public bool ShowCode
    {
        get => _showCode;
        private set { SetProperty(ref _showCode, value); RefreshInitialStatusVisibility(); }
    }

    public bool ShowProgress
    {
        get => _showProgress;
        private set { SetProperty(ref _showProgress, value); RefreshInitialStatusVisibility(); }
    }

    public bool IsConnectingToServer
    {
        get => _isConnectingToServer;
        private set { SetProperty(ref _isConnectingToServer, value); RefreshInitialStatusVisibility(); }
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set { SetProperty(ref _isComplete, value); RefreshInitialStatusVisibility(); }
    }

    /// <summary>
    /// True only before the pairing code, progress, or completion sections
    /// have appeared. Prevents the initial "Starting…"/"Connecting…" label
    /// from re-showing (and duplicating StatusMessage) once progress begins.
    /// </summary>
    public bool ShowInitialStatus { get => _showInitialStatus; private set => SetProperty(ref _showInitialStatus, value); }

    public string? CompletedPath { get => _completedPath; private set => SetProperty(ref _completedPath, value); }

    /// <summary>Current smoothed transfer rate, formatted for display (e.g. "2.4 MB/s").</summary>
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }

    /// <summary>Estimated time remaining, formatted for display (e.g. "1:32 remaining").</summary>
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }

    /// <summary>Bytes received so far vs. total, formatted for display (e.g. "12.3 MB / 45.0 MB").</summary>
    public string TransferredText { get => _transferredText; private set => SetProperty(ref _transferredText, value); }

    /// <summary>
    /// True when this VM is displaying an instant (nearby-peer) receive driven by
    /// <see cref="BeginInstantReceive"/> rather than the pairing-code/signaling flow started by <see cref="StartAsync"/>.
    /// This VM is a DI singleton so <see cref="Pages.ReceivePage"/> checks this flag on appearing to avoid
    /// starting a redundant signaling flow while an instant transfer is already in progress.
    /// </summary>
    public bool IsInstantMode { get => _isInstantMode; private set => SetProperty(ref _isInstantMode, value); }

    public ReceiveViewModel(ISignalingClient signaling, SignalingConnectionManager connectionManager,
        TransferRepository repo, SettingsViewModel settings, ILogger<WebRtcChannel> webRtcLogger)
    {
        _signaling = signaling;
        _connectionManager = connectionManager;
        _repo = repo;
        _settings = settings;
        _webRtcLogger = webRtcLogger;
    }

    private void RefreshInitialStatusVisibility()
        => ShowInitialStatus = !IsConnectingToServer && !ShowCode && !ShowProgress && !IsComplete;

    /// <summary>
    /// Clears all display state back to defaults. Called at the start of every fresh receive
    /// (signaling or instant) since this VM is a singleton and must not leak a prior transfer's
    /// state (e.g. a lingering "Transfer complete!") into the next one.
    /// </summary>
    private void ResetState()
    {
        IsInstantMode = false;
        PairingCode = "";
        StatusMessage = "Starting...";
        ProgressValue = 0;
        ShowCode = false;
        ShowProgress = false;
        IsComplete = false;
        IsConnectingToServer = false;
        CompletedPath = null;
        SpeedText = "—";
        RemainingText = "calculating…";
        TransferredText = "";
    }

    /// <summary>Builds a throttled <see cref="TransferProgress"/> reporter that updates this VM's display fields.</summary>
    private ThrottledProgress<TransferProgress> CreateThrottledProgress()
    {
        var rateTracker = new TransferRateTracker();
        var progress = new Progress<TransferProgress>(p =>
        {
            ProgressValue = p.Percentage / 100.0;
            StatusMessage = $"Receiving... {p.Percentage:F0}%";

            var snapshot = rateTracker.Sample(p.BytesSent, p.TotalBytes, DateTimeOffset.UtcNow);
            SpeedText = ByteFormatter.FormatRate(snapshot.BytesPerSecond);
            RemainingText = snapshot.EtaSeconds is { } eta
                ? $"{ByteFormatter.FormatDuration(eta)} remaining"
                : "calculating…";
            TransferredText = $"{ByteFormatter.Format(p.BytesSent)} / {ByteFormatter.Format(p.TotalBytes)}";
        });
        return new ThrottledProgress<TransferProgress>(
            progress, TimeSpan.FromMilliseconds(500), p => p.BytesSent >= p.TotalBytes);
    }

    /// <summary>
    /// Switches this VM into instant-receive mode: resets prior state and shows a progress screen
    /// for a transfer that <see cref="Services.IncomingTransferCoordinator"/> is already driving.
    /// </summary>
    public void BeginInstantReceive(string senderName)
    {
        ResetState();
        IsInstantMode = true;
        ShowProgress = true;
        StatusMessage = $"Receiving from {senderName}…";
    }

    /// <summary>Builds the progress reporter an instant-receive caller feeds into <c>TransferSession.ReceiveAsync</c>.</summary>
    public IProgress<TransferProgress> CreateInstantReceiveProgress() => CreateThrottledProgress();

    /// <summary>Marks an instant receive as successfully completed.</summary>
    public void CompleteInstantReceive(string outputRoot)
    {
        CompletedPath = outputRoot;
        IsComplete = true;
        ShowProgress = false;
        StatusMessage = "Transfer complete!";
    }

    /// <summary>Marks an instant receive as failed, showing the error inline (no popup).</summary>
    public void FailInstantReceive(string message)
    {
        StatusMessage = $"Error: {message}";
        ShowProgress = false;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ResetState();
        try
        {
            IsConnectingToServer = true;
            EdzioLog.Info("ReceiveVM", "Waiting for signaling connection...");
            await _connectionManager.WaitForConnectedAsync(ct);
            IsConnectingToServer = false;
            EdzioLog.Info("ReceiveVM", "Signaling connection ready");

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

            EdzioLog.Info("ReceiveVM", "Negotiating transfer channel (LAN-direct + WebRTC race)...");
            await using var channel = await TransferChannelNegotiator.ConnectAsReceiverAsync(
                rtcConfig, _signaling, _webRtcLogger, ct);
            EdzioLog.Info("ReceiveVM", $"Channel established: {channel.GetType().Name}");

            var outputRoot = _settings.DownloadLocation;
            Directory.CreateDirectory(outputRoot);

            var throttledProgress = CreateThrottledProgress();

            await TransferSession.ReceiveAsync(outputRoot, "Sender", channel, _repo, throttledProgress, ct);

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
            EdzioLog.Error("ReceiveVM", "Receive failed", ex);
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
