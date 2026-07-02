using Edzio.Desktop.ViewModels;

namespace Edzio.Desktop.Services;

public enum ServerStatus { Unknown, Checking, Online, Offline }

/// <summary>
/// Periodically probes the signaling server's /health endpoint and publishes
/// the result. Registered as a singleton; call <see cref="Start"/> once on
/// app launch. Re-checks immediately whenever the configured URL changes.
/// </summary>
public sealed class SignalingHealthMonitor : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SettingsViewModel _settings;
    private readonly PeriodicTimer _timer;
    private CancellationTokenSource _cts = new();
    private Task? _loop;

    private ServerStatus _status = ServerStatus.Unknown;
    public ServerStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? StatusChanged;

    public SignalingHealthMonitor(SettingsViewModel settings)
    {
        _settings = settings;
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        // Re-probe immediately when the user changes the server URL
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SignalingServerUrl))
                Restart();
        };
    }

    /// <summary>Starts the background polling loop.</summary>
    public void Start()
    {
        _loop = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // First check immediately, then on every timer tick
        await CheckAsync(ct);
        try
        {
            while (await _timer.WaitForNextTickAsync(ct))
                await CheckAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        Status = ServerStatus.Checking;
        try
        {
            var url = _settings.SignalingServerUrl.TrimEnd('/') + "/health";
            var response = await _http.GetAsync(url, ct);
            Status = response.IsSuccessStatusCode ? ServerStatus.Online : ServerStatus.Offline;
        }
        catch
        {
            Status = ServerStatus.Offline;
        }
    }

    /// <summary>Cancels the current loop and restarts it (used when URL changes).</summary>
    private void Restart()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer.Dispose();
        _http.Dispose();
    }
}
