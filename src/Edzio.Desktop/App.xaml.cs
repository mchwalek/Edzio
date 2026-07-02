namespace Edzio.Desktop;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var services = IPlatformApplication.Current!.Services;

        // Ensure DB schema exists on first run
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Core.Persistence.TransferDbContext>();
        db.Database.EnsureCreated();

        return new Window(services.GetRequiredService<AppShell>());
    }
}
