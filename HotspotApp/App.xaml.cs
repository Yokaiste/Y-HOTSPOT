using HotspotApp.Constants;
using HotspotApp.Localization;
using HotspotApp.Services;
using HotspotApp.Services.UI;
using HotspotApp.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace HotspotApp;

public partial class App : Application
{
    public static bool StartInTray { get; set; }
    public static bool StartInSettings { get; set; }
    public static MainWindow? CurrentWindow { get; set; }
    public static IServiceProvider Services => AppServices.Services;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var windowService = Services.GetRequiredService<WindowService>();
        windowService.ShowMainWindow(StartInTray);
    }

    private void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args
    )
    {
        var message = AppStrings.Format(
            "Text_AppStartFailedMessage",
            AppConstants.AppDisplayName,
            args.Exception.Message
        );
        var title = AppStrings.Get("Text_FatalErrorTitle");
        UiExceptionService.HandleUnhandledException(
            args.Exception,
            "ApplicationUnhandledException",
            message,
            title
        );
        args.Handled = true;
    }
}
