using HotspotApp.Logging;
using HotspotApp.Services.Core;
using HotspotApp.Services.UI;
using HotspotApp.ViewModels;
using HotspotApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HotspotApp.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.AddLogging(builder =>
            LoggingBootstrapper.ConfigureFileLogging(builder, LogPaths.GetUiLogPath())
        );

        // Shared Services
        services.AddSharedServices();

        // UI Services
        services.AddSingleton<FileExplorerService>();
        services.AddSingleton<TrayIconManager>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<HotspotApp.Services.Infrastructure.ManualHotspotSession>();
        services.AddSingleton<HotspotApp.Services.Infrastructure.WslDnsRepairService>();
        services.AddSingleton<ErrorHistoryService>();

        // Transient Services
        services.AddTransient<ServiceClient>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<StatusViewModel>();
        services.AddTransient<TroubleshootingViewModel>();
        services.AddTransient<MainWindow>();

        return services;
    }
}
