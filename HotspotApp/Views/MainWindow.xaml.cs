using HotspotApp.Constants;
using HotspotApp.Helpers;
using HotspotApp.Localization;
using HotspotApp.Models;
using HotspotApp.Services.Core;
using HotspotApp.Services.UI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HotspotApp.Views;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly WindowCustomizer _windowCustomizer;
    private readonly NavigationService _navigationService;
    private readonly ServiceStateManager _serviceStateManager;
    private readonly DialogService _dialogService;
    private readonly TrayIconManager _trayIconManager;
    private readonly ILogger<MainWindow> _logger;
    private readonly WindowMessageMonitor _monitor;
    private readonly uint _showAppMessage;

    public IReadOnlyList<NavigationItem> NavigationItems => _navigationService.Items;
    public bool IsWindowVisible => _appWindow.IsVisible;

    private readonly ServiceManager _serviceManager;

    public MainWindow(
        ServiceManager serviceManager,
        NavigationService navigationService,
        ServiceStateManager serviceStateManager,
        DialogService dialogService,
        TrayIconManager trayIconManager,
        ILogger<MainWindow> logger
    )
    {
        _serviceManager = serviceManager;
        _navigationService = navigationService;
        _serviceStateManager = serviceStateManager;
        _dialogService = dialogService;
        _trayIconManager = trayIconManager;
        _logger = logger;

        InitializeComponent();
        RootGrid.Loaded += RootGrid_Loaded;

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _windowCustomizer = new WindowCustomizer(this, _appWindow);
        _windowCustomizer.InitializeWindowStyle();
        Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(_appWindow.Id)
            .ExitedMoveSize += (_, _) => _windowCustomizer.RememberPosition();

        var windowIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
        if (File.Exists(windowIconPath))
        {
            _appWindow.SetIcon(windowIconPath);
        }

        Title = AppConstants.AppDisplayName;
        _appWindow.Closing += AppWindow_Closing;

        _monitor = new WindowMessageMonitor(this);
        _monitor.Initialize();
        _monitor.MessageReceived += OnWindowMessage;

        _trayIconManager.Initialize(this, _monitor);

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _showAppMessage = WindowInterop.RegisterWindowMessage("WM_SHOW_HOTSPOT_APP");
    }

    private void OnWindowMessage(object? sender, WindowMessageEventArgs e)
    {
        if (e.Msg == _showAppMessage && _showAppMessage != 0)
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                _windowCustomizer.UpdateAppPosition();
                _windowCustomizer.EnsureWindowVisible();
                await _windowCustomizer.AnimateWindowEntranceAsync();
            });
        }

        if (e.Msg == WindowInterop.WmDpiChanged || e.Msg == WindowInterop.WmDisplayChange)
        {
            QueuePositionUpdate();
            _ = DispatcherQueue.TryEnqueue(UpdateTitleDragRegion);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        QueuePositionUpdate();
    }

    private void OnUserPreferenceChanged(
        object sender,
        Microsoft.Win32.UserPreferenceChangedEventArgs e
    )
    {
        if (
            e.Category == Microsoft.Win32.UserPreferenceCategory.Desktop
            || e.Category == Microsoft.Win32.UserPreferenceCategory.General
        )
        {
            QueuePositionUpdate();
        }
    }

    private void QueuePositionUpdate()
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            // Add a delay to ensure display topology is updated
            await Task.Delay(200);
            _windowCustomizer.UpdateAppPosition();
        });
    }

    public void EnsureWindowVisible()
    {
        _windowCustomizer.EnsureWindowVisible();
    }

    public void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = App.StartInSettings
            ? NavigationItems.First(item => item.PageType == typeof(SettingsPage))
            : NavigationItems[0];
        App.StartInSettings = false;
        _ = EnsureServiceVersionMatchAsync();
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateTitleDragRegion();
        if (RootGrid.XamlRoot != null)
        {
            _dialogService.SetXamlRoot(RootGrid.XamlRoot);
        }
    }

    public void SetUiLocked(bool locked)
    {
        if (RootGrid == null)
        {
            return;
        }

        RootGrid.IsHitTestVisible = !locked;
        RootGrid.Opacity = locked ? 0.6 : 1.0;
    }

    public void SetProcessingState(bool isProcessing)
    {
        _trayIconManager.SetProcessingState(isProcessing);
    }

    public void ShowExecutableNotFound()
    {
        _ = ShowErrorAsync(AppStrings.Get("Text_ExecutableNotFoundMessage"));
    }

    public void ShowServiceError(Exception ex, string logEvent)
    {
        LogServiceError(ex, logEvent);
        _ = ShowErrorAsync(AppStrings.Format("Text_ServiceErrorFormat", ex.Message));
    }

    private Task<bool> ShowErrorAsync(string message)
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (
            !DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    EnsureWindowVisible();
                    await _windowCustomizer.AnimateWindowEntranceAsync();
                    await _dialogService.ShowErrorAsync(message);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    LogShowErrorFailed(ex);
                    tcs.TrySetException(ex);
                }
            })
        )
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    private async Task EnsureServiceVersionMatchAsync()
    {
        try
        {
            if (await _serviceManager.IsServiceInstalledAsync(AppConstants.AppName))
            {
                await _serviceStateManager.ApplyAsync(true);
                await _trayIconManager.UpdateTrayMenuStateAsync();
            }
        }
        catch (Exception ex)
        {
            LogServiceVersionCheckFailed(ex);
            UiExceptionService.LogUnhandledException(ex, "ServiceVersionCheckFailed");
        }
    }

    public void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args
    )
    {
        if (args.SelectedItem is NavigationItem item)
        {
            NavigationService.Navigate(ContentFrame, item);
        }
    }

    public void PinWindow_Click(object sender, RoutedEventArgs e)
    {
        try { _windowCustomizer.SetPinned(PinWindowButton.IsChecked == true); }
        catch (Exception ex)
        {
            PinWindowButton.IsChecked = _windowCustomizer.IsPinned;
            UiExceptionService.LogUnhandledException(ex, "SetWindowPinnedFailed");
        }
    }

    public void RestorePosition_Click(object sender, RoutedEventArgs e)
        => _windowCustomizer.RestorePosition();

    public void TitleDragRegion_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTitleDragRegion();

    private void UpdateTitleDragRegion()
    {
        if (_appWindow == null || TitleDragRegion.XamlRoot == null) return;
        var scale = TitleDragRegion.XamlRoot.RasterizationScale;
        var origin = TitleDragRegion.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point());
        // Register only the title area; pin, restore and close remain ordinary buttons.
        var input = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
        input.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption,
            [new Windows.Graphics.RectInt32((int)(origin.X * scale), (int)(origin.Y * scale),
                (int)(TitleDragRegion.ActualWidth * scale), (int)(TitleDragRegion.ActualHeight * scale))]);
    }

    public void CloseToTray_Click(object sender, RoutedEventArgs e)
    {
        _ = HideToTrayAsync();
    }

    public void AboutItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        _ = _dialogService.ShowAboutAsync();
    }

    public async Task HideToTrayAsync()
    {
        LogWindowHideToTray();
        await _windowCustomizer.AnimateWindowExitAsync();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{LogEvent}")]
    private partial void LogServiceError(Exception ex, string logEvent);

    [LoggerMessage(Level = LogLevel.Error, Message = "ShowErrorFailed")]
    private partial void LogShowErrorFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "ServiceVersionCheckFailed")]
    private partial void LogServiceVersionCheckFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "WindowHideToTray")]
    private partial void LogWindowHideToTray();

    public async Task<TrayExitChoice> ShowExitConfirmationAsync()
    {
        if (Content is not FrameworkElement root)
        {
            return TrayExitChoice.Cancel;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = root.XamlRoot,
            Title = AppStrings.Get("Text_ExitDialogTitle"),
            Content = AppStrings.Get("Text_ExitDialogContent"),
            PrimaryButtonText = AppStrings.Get("Text_ExitDialogExitButtonText"),
            SecondaryButtonText = AppStrings.Get("Text_ExitDialogExitDisableButtonText"),
            CloseButtonText = AppStrings.Get("Text_ExitDialogCancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => TrayExitChoice.Exit,
            ContentDialogResult.Secondary => TrayExitChoice.ExitAndDisableService,
            _ => TrayExitChoice.Cancel,
        };
    }

    public void RefreshSettingsPage()
    {
        if (NavView.Content is Frame frame && frame.Content is SettingsPage settingsPage)
        {
            _ = settingsPage.ViewModel.RefreshServiceStatusAsync();
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        _ = HideToTrayAsync();
    }

    public void InitializeEntrance()
    {
        _windowCustomizer.InitializeEntrance();
    }

    public async Task AnimateWindowEntranceAsync()
    {
        await _windowCustomizer.AnimateWindowEntranceAsync();
    }

    public void AnimateWindowEntrance()
    {
        _ = AnimateWindowEntranceAsync();
    }

    public void ShowAppWindow()
    {
        _appWindow.Show();
    }

    public void HideAppWindow()
    {
        _appWindow.Hide();
    }
}
