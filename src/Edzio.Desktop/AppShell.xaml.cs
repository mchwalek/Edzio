using Edzio.Desktop.Services;
using Microsoft.Maui.Graphics;

namespace Edzio.Desktop;

public partial class AppShell : Shell
{
    private static readonly Color ColorOnline  = Color.FromArgb("#107C10");
    private static readonly Color ColorOffline = Color.FromArgb("#C42B1C");
    private static readonly Color ColorUnknown = Color.FromArgb("#888888");

    public AppShell(IServiceProvider services)
    {
        InitializeComponent();

        // Resolve the home page from DI so its constructor-injected ViewModel is provided.
        HomeContent.Content = services.GetRequiredService<Pages.HomePage>();

        Routing.RegisterRoute("send",     typeof(Pages.SendPage));
        Routing.RegisterRoute("receive",  typeof(Pages.ReceivePage));
        Routing.RegisterRoute("settings", typeof(Pages.SettingsPage));

        // Wire up signaling health indicator
        var monitor = services.GetRequiredService<SignalingHealthMonitor>();
        monitor.StatusChanged += (_, _) => UpdateStatusUi(monitor.Status);
        UpdateStatusUi(monitor.Status); // set initial state
        monitor.Start();
    }

    private void UpdateStatusUi(ServerStatus status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            (StatusDot.Fill, StatusLabel.Text) = status switch
            {
                ServerStatus.Online   => (new SolidColorBrush(ColorOnline),  "Relay: Online"),
                ServerStatus.Offline  => (new SolidColorBrush(ColorOffline), "Relay: Offline"),
                ServerStatus.Checking => (new SolidColorBrush(ColorUnknown), "Relay: Checking\u2026"),
                _                     => (new SolidColorBrush(ColorUnknown), "Relay: Unknown"),
            };
        });
    }

    private async void OnStatusTapped(object? sender, TappedEventArgs e)
        => await GoToAsync("settings");
}
