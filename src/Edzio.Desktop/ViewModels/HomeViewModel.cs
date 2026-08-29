using System.Collections.ObjectModel;
using System.Windows.Input;
using Edzio.Core.Discovery;

namespace Edzio.Desktop.ViewModels;

public class HomeViewModel : BaseViewModel
{
    private readonly ILocalDiscovery _discovery;

    public ObservableCollection<LocalPeer> NearbyPeers { get; } = new();
    public ICommand ReceiveCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand SendToLocalPeerCommand { get; }
    public ICommand SettingsCommand { get; }

    public HomeViewModel(ILocalDiscovery discovery)
    {
        _discovery = discovery;
        Title = "Edzio";

        ReceiveCommand = new Command(async () => await Shell.Current.GoToAsync("receive"));
        SendCommand = new Command(async () => await Shell.Current.GoToAsync("send"));
        SettingsCommand = new Command(async () => await Shell.Current.GoToAsync("settings"));
        SendToLocalPeerCommand = new Command<LocalPeer>(async peer =>
            await Shell.Current.GoToAsync($"send?localPeerId={Uri.EscapeDataString(peer.InstanceId)}"));

        _discovery.PeersChanged += (_, peers) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                NearbyPeers.Clear();
                foreach (var p in peers) NearbyPeers.Add(p);
            });
    }

    // Discovery now runs app-wide from launch via IncomingTransferCoordinator; these are kept as
    // no-ops because HomePage.xaml.cs calls them from page lifecycle overrides.
    public Task OnAppearingAsync() => Task.CompletedTask;
    public Task OnDisappearingAsync() => Task.CompletedTask;
}
