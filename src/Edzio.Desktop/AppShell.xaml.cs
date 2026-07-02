namespace Edzio.Desktop;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();

        // Resolve the home page from DI so its constructor-injected ViewModel is provided.
        // ContentTemplate="{DataTemplate ...}" bypasses DI and calls the parameterless
        // constructor, which doesn't exist on pages that require injected services.
        HomeContent.Content = services.GetRequiredService<Pages.HomePage>();

        Routing.RegisterRoute("send", typeof(Pages.SendPage));
        Routing.RegisterRoute("receive", typeof(Pages.ReceivePage));
        Routing.RegisterRoute("settings", typeof(Pages.SettingsPage));
    }
}
