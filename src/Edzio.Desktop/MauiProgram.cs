using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using Edzio.Core.Discovery;
using Edzio.Core.Persistence;
using Edzio.Core.Signaling;
using Edzio.Core.Transfer;
using Edzio.Desktop.Pages;
using Edzio.Desktop.Services;
using Edzio.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Edzio.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Phosphor-Fill.ttf", "PhosphorFill");
            });

        // File logger — always on, captures SIPSorcery internals too
        builder.Logging.AddProvider(new EdzioLoggerProvider());
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#if DEBUG
        builder.Logging.AddDebug();
#endif

        // Database
        var dbPath = AppPaths.DatabasePath;
        builder.Services.AddDbContext<TransferDbContext>(o => o.UseSqlite($"DataSource={dbPath}"));
        builder.Services.AddScoped<TransferRepository>();

        // Core services
        builder.Services.AddSingleton<ISignalingClient, SignalingClient>();
        builder.Services.AddSingleton<MdnsDiscovery>(sp => new MdnsDiscovery());
        builder.Services.AddSingleton<ILocalDiscovery>(sp => sp.GetRequiredService<MdnsDiscovery>());
        builder.Services.AddSingleton<InstantReceiveService>();
        builder.Services.AddSingleton<SignalingConnectionManager>();
        builder.Services.AddSingleton<ConnectionStatusViewModel>();
        builder.Services.AddSingleton<IFolderPicker>(FolderPicker.Default);

        // ViewModels (SettingsViewModel is singleton because SignalingServerUrl is read by other VMs)
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddTransient<HomeViewModel>();
        builder.Services.AddTransient<SendViewModel>();
        builder.Services.AddTransient<ReceiveViewModel>();

        // Shell (singleton — only one instance ever needed)
        builder.Services.AddSingleton<AppShell>();

        // Pages
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<SendPage>();
        builder.Services.AddTransient<ReceivePage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
