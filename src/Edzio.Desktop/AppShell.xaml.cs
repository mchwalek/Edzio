using Edzio.Core.Signaling;
using Edzio.Desktop.ViewModels;

namespace Edzio.Desktop;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();

        // Resolve the home page from DI so its constructor-injected ViewModel is provided.
        HomeContent.Content = services.GetRequiredService<Pages.HomePage>();

        Routing.RegisterRoute("send",     typeof(Pages.SendPage));
        Routing.RegisterRoute("receive",  typeof(Pages.ReceivePage));
        Routing.RegisterRoute("settings", typeof(Pages.SettingsPage));

        // Wire up the app-wide connection status bar.
        BindingContext = services.GetRequiredService<ConnectionStatusViewModel>();

        // Connect to the signaling server eagerly on launch, and reconnect whenever the
        // user changes the server URL in Settings.
        var connectionManager = services.GetRequiredService<SignalingConnectionManager>();
        var settings = services.GetRequiredService<SettingsViewModel>();
        connectionManager.Start(settings.SignalingServerUrl);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SignalingServerUrl))
                connectionManager.UpdateUrl(settings.SignalingServerUrl);
        };
    }
}
