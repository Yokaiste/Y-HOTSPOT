using HotspotApp.Constants;
using HotspotApp.Localization;
using HotspotApp.Logging;
using HotspotApp.Services.UI;
using Microsoft.UI.Dispatching;

namespace HotspotApp.Services;

public static class UiLauncher
{
    public static void Run(string[] args)
    {
        App.StartInTray = args.Contains("--tray");
        App.StartInSettings = args.Contains("--settings");
        LanguagePreferences.Initialize();

        LoggingBootstrapper.InitializeLogLevelAsync().GetAwaiter().GetResult();
        AppServices.Initialize();
        ManualTestDiagnostics.Write("Interface iniciada");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                UiExceptionService.LogUnhandledException(exception, "AppDomainUnhandledException");
            }
            else
            {
                UiExceptionService.LogUnhandledException(
                    new Exception("Unhandled exception"),
                    "AppDomainUnhandledException"
                );
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            UiExceptionService.LogUnhandledException(
                eventArgs.Exception,
                "UnobservedTaskException"
            );
            eventArgs.SetObserved();
        };

        global::WinRT.ComWrappersSupport.InitializeComWrappers();

        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread()
            );
            SynchronizationContext.SetSynchronizationContext(context);

            try
            {
                _ = new App();
            }
            catch (Exception ex)
            {
                var message = AppStrings.Format(
                    "Text_AppStartFailedMessage",
                    AppConstants.AppDisplayName,
                    ex.Message
                );
                var title = AppStrings.Get("Text_FatalErrorTitle");
                UiExceptionService.HandleUnhandledException(ex, "AppOpenFailed", message, title);
            }
        });
    }

}
