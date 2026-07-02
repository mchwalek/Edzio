using CommunityToolkit.Maui.Storage;
using Edzio.Desktop.Services;
using Microsoft.Maui.Storage;

namespace Edzio.Desktop.ViewModels;

public class SettingsViewModel : BaseViewModel
{
    private const string KeyUrl = "signalingUrl";
    private const string KeyTurnUrl = "turnUrl";
    private const string KeyTurnUser = "turnUser";
    private const string KeyTurnCred = "turnCred";
    private const string KeyDownloadLocation = "downloadLocation";
    public const string DefaultSignalingUrl = "https://signal.edzio.app";

    private readonly IFolderPicker _folderPicker;

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

    /// <summary>Folder where received files are saved. Defaults to the system Downloads folder.</summary>
    public string DownloadLocation
    {
        get => Preferences.Default.Get(KeyDownloadLocation, AppPaths.DefaultDownloadDirectory);
        set { Preferences.Default.Set(KeyDownloadLocation, value); OnPropertyChanged(); }
    }

    public System.Windows.Input.ICommand ResetCommand { get; }
    public System.Windows.Input.ICommand PickDownloadLocationCommand { get; }
    public System.Windows.Input.ICommand ResetDownloadLocationCommand { get; }

    public SettingsViewModel(IFolderPicker folderPicker)
    {
        _folderPicker = folderPicker;
        Title = "Settings";
        ResetCommand = new Command(() => SignalingServerUrl = DefaultSignalingUrl);
        PickDownloadLocationCommand = new Command(async () => await PickDownloadLocationAsync());
        ResetDownloadLocationCommand = new Command(() => DownloadLocation = AppPaths.DefaultDownloadDirectory);
    }

    private async Task PickDownloadLocationAsync()
    {
        var result = await _folderPicker.PickAsync();
        if (result.IsSuccessful)
            DownloadLocation = result.Folder.Path;
    }
}
