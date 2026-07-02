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
            await Shell.Current.GoToAsync(
                $"send?localPeerIp={Uri.EscapeDataString(peer.IpAddress)}&localPeerPort={peer.Port}&localPeerName={Uri.EscapeDataString(peer.DisplayName)}"));

        _discovery.PeersChanged += (_, peers) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                NearbyPeers.Clear();
                foreach (var p in peers) NearbyPeers.Add(p);
            });
    }

    public async Task OnAppearingAsync() => await _discovery.StartAsync();
    public async Task OnDisappearingAsync() => await _discovery.StopAsync();
}
