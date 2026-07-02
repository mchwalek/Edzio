using Microsoft.Maui.Storage;
namespace Edzio.Desktop.ViewModels;

public class SettingsViewModel : BaseViewModel
{
    private const string KeyUrl = "signalingUrl";
    private const string KeyTurnUrl = "turnUrl";
    private const string KeyTurnUser = "turnUser";
    private const string KeyTurnCred = "turnCred";
    public const string DefaultSignalingUrl = "https://signal.edzio.app";

    public string SignalingServerUrl
    {
        get => Preferences.Default.Get(KeyUrl, DefaultSignalingUrl);
        set { Preferences.Default.Set(KeyUrl, value); OnPropertyChanged(); }
    }
    public string TurnServerUrl
    {
        get => Preferences.Default.Get(KeyTurnUrl, "");
        set { Preferences.Default.Set(KeyTurnUrl, value); OnPropertyChanged(); }
    }
    public string TurnUsername
    {
        get => Preferences.Default.Get(KeyTurnUser, "");
        set { Preferences.Default.Set(KeyTurnUser, value); OnPropertyChanged(); }
    }
    public string TurnCredential
    {
        get => Preferences.Default.Get(KeyTurnCred, "");
        set { Preferences.Default.Set(KeyTurnCred, value); OnPropertyChanged(); }
    }
    public System.Windows.Input.ICommand ResetCommand { get; }

    public SettingsViewModel()
    {
        Title = "Settings";
        ResetCommand = new Command(() => SignalingServerUrl = DefaultSignalingUrl);
    }
}
