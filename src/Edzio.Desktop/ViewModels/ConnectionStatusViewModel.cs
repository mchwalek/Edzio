using System.Windows.Input;
using Edzio.Core.Signaling;
using Microsoft.Maui.Graphics;

namespace Edzio.Desktop.ViewModels;

/// <summary>
/// Drives the app-wide connection status bar shown in <c>AppShell</c>'s title area.
/// Wraps a <see cref="SignalingConnectionManager"/>'s state into bindable properties.
/// Hidden unless the connection is reconnecting or has failed.
/// </summary>
public class ConnectionStatusViewModel : BaseViewModel
{
    private readonly SignalingConnectionManager _manager;

    private bool _isBarVisible;
    private bool _isFailed;
    private bool _isReconnecting;
    private string _message = "";
    private Color _barColor = Colors.Transparent;

    public bool IsBarVisible { get => _isBarVisible; private set => SetProperty(ref _isBarVisible, value); }
    public bool IsFailed { get => _isFailed; private set => SetProperty(ref _isFailed, value); }
    public bool IsReconnecting { get => _isReconnecting; private set => SetProperty(ref _isReconnecting, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public Color BarColor { get => _barColor; private set => SetProperty(ref _barColor, value); }

    public ICommand TryAgainCommand { get; }

    public ConnectionStatusViewModel(SignalingConnectionManager manager)
    {
        _manager = manager;
        TryAgainCommand = new Command(() => _manager.RetryNow());
        manager.StateChanged += (_, state) => MainThread.BeginInvokeOnMainThread(() => Apply(state));
        Apply(manager.State);
    }

    private void Apply(ConnectionManagerState state)
    {
        switch (state)
        {
            case ConnectionManagerState.Reconnecting:
                IsBarVisible = true;
                IsFailed = false;
                IsReconnecting = true;
                Message = "Trying to reconnect\u2026";
                BarColor = (Color)Application.Current!.Resources["StatusWarning"];
                break;
            case ConnectionManagerState.Failed:
                IsBarVisible = true;
                IsFailed = true;
                IsReconnecting = false;
                Message = "\u26a0  Can't connect right now. Transfers to distant devices are paused. Retrying\u2026";
                BarColor = (Color)Application.Current!.Resources["StatusError"];
                break;
            default: // Connecting, Connected
                IsBarVisible = false;
                IsFailed = false;
                IsReconnecting = false;
                break;
        }
    }
}
