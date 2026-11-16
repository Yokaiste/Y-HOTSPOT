using HotspotApp.Config;
using HotspotApp.Constants;
using HotspotApp.Helpers;
using HotspotApp.Localization;
using HotspotApp.Models;
using HotspotApp.Services.Core;
using HotspotApp.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Application = Microsoft.UI.Xaml.Application;
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace HotspotApp.Services.UI;

public partial class TrayIconManager(
    ServiceManager serviceManager,
    ServiceStateManager serviceStateManager,
    ServiceClient serviceClient,
    FileExplorerService fileExplorerService,
    DialogService dialogService,
    ErrorHistoryService errorHistoryService,
    ILogger<TrayIconManager> logger
) : IDisposable
{
    private TrayIconNative? _native;
    private MenuFlyout? _trayMenu;
    private MenuFlyoutItem? _serviceToggleItem;
    private MenuFlyoutItem? _restartHotspotItem;
    private MenuFlyoutItem? _qrItem;

    private MainWindow? _mainWindow;
    private bool _trayHostTemporarilyShown;
    private int _trayHostExStyle;
    private bool _trayMenuTopmostApplied;
    private int _trayMenuExStyle;
    private bool _isProcessingTrayAction;

    public void Initialize(MainWindow mainWindow, WindowMessageMonitor monitor)
    {
        _mainWindow = mainWindow;
        _native = new TrayIconNative(mainWindow, monitor);
        _native.LeftClick += OnTrayLeftClick;
        _native.RightClick += OnTrayRightClick;
        _native.Initialize();
        _ = UpdateTrayMenuStateAsync();
    }

    public void Dispose()
    {
        _native?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnTrayLeftClick()
    {
        _ = ToggleWindowOnUiAsync();
    }

    private void OnTrayRightClick()
    {
        _ = RunOnUiThreadAsync(async () =>
        {
            await UpdateTrayMenuStateAsync();
            ShowTrayMenuAtCursor();
        });
    }

    public void SetProcessingState(bool isProcessing)
    {
        if (_isProcessingTrayAction == isProcessing)
            return;
        _isProcessingTrayAction = isProcessing;
        if (_mainWindow?.Content is FrameworkElement)
        {
            _mainWindow.SetUiLocked(isProcessing);
        }
        _ = UpdateTrayMenuStateAsync();
    }

    public async Task UpdateTrayMenuStateAsync()
    {
        try
        {
            EnsureTrayMenu();

            if (_isProcessingTrayAction)
            {
                SetMenuItemsVisibility(Visibility.Collapsed);
                return;
            }

            var isInstalled = await serviceManager.IsServiceInstalledAsync(AppConstants.AppName);
            var status = isInstalled
                ? await serviceManager.GetServiceStatusAsync(AppConstants.AppName)
                : ServiceStatus.NotInstalled;
            var isRunning = status == ServiceStatus.Running;

            var statusText = status switch
            {
                ServiceStatus.Running => AppStrings.Get("Text_ServiceStatusRunning"),
                ServiceStatus.Stopped => AppStrings.Get("Text_ServiceStatusStopped"),
                ServiceStatus.NotInstalled => AppStrings.Get("Text_ServiceStatusNotInstalled"),
                ServiceStatus.Error => AppStrings.Get("Text_ServiceErrorMessage"),
                _ => AppStrings.Get("Text_StatusUnknown"),
            };

            var tooltip = AppStrings.Format(
                "Text_TrayTooltipFormat",
                AppConstants.AppDisplayName,
                statusText
            );
            _native?.UpdateTooltip(tooltip);

            UpdateMenuItems(isInstalled, isRunning);
        }
        catch { }
    }

    private void SetMenuItemsVisibility(Visibility visibility)
    {
        if (_serviceToggleItem != null)
            _serviceToggleItem.Visibility = visibility;
        if (_restartHotspotItem != null)
            _restartHotspotItem.Visibility = visibility;
        if (_qrItem != null)
            _qrItem.Visibility = visibility;
    }

    private void UpdateMenuItems(bool isInstalled, bool isRunning)
    {
        if (_serviceToggleItem != null)
        {
            _serviceToggleItem.Visibility = Visibility.Visible;
            _serviceToggleItem.Text = isInstalled
                ? AppStrings.Get("Text_TrayDisableServiceText")
                : AppStrings.Get("Text_TrayEnableServiceText");
        }

        if (_restartHotspotItem != null)
        {
            _restartHotspotItem.IsEnabled = isRunning;
            _restartHotspotItem.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_qrItem != null)
        {
            _qrItem.IsEnabled = isRunning;
            _qrItem.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EnsureTrayMenu()
    {
        if (_trayMenu != null)
        {
            return;
        }

        _trayMenu = new MenuFlyout();
        _trayMenu.Closed += (s, e) =>
        {
            RestoreTrayMenuTopmost();
            if (_trayHostTemporarilyShown)
            {
                RestoreTrayHost(false);
            }
        };

        var openItem = CreateTrayItem(AppStrings.Get("Text_TrayOpenText"), ShowWindowOnUiAsync);
        _serviceToggleItem = CreateTrayItem(
            AppStrings.Get("Text_TrayEnableServiceText"),
            ToggleServiceFromTrayAsync
        );
        _restartHotspotItem = CreateTrayItem(
            AppStrings.Get("Text_TrayRestartHotspotText"),
            RestartHotspotFromTrayAsync
        );
        var viewLogsItem = CreateTrayItem(
            AppStrings.Get("Text_TrayViewLogsText"),
            onClick: OpenLogs
        );
        _qrItem = CreateTrayItem(AppStrings.Get("Text_TrayShowQrText"), ShowQrFromTrayAsync);
        var exitItem = CreateTrayItem(AppStrings.Get("Text_TrayExitText"), ExitFromTrayAsync);

        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(_serviceToggleItem);
        _trayMenu.Items.Add(_restartHotspotItem);
        _trayMenu.Items.Add(viewLogsItem);
        _trayMenu.Items.Add(_qrItem);
        _trayMenu.Items.Add(exitItem);
    }

    private static MenuFlyoutItem CreateTrayItem(
        string text,
        Func<Task>? onClickAsync = null,
        Action? onClick = null
    )
    {
        var item = new MenuFlyoutItem { Text = text };
        if (onClickAsync != null)
        {
            item.Click += async (s, e) => await onClickAsync();
        }
        else if (onClick != null)
        {
            item.Click += (s, e) => onClick();
        }

        return item;
    }

    private void ShowTrayMenuAtCursor()
    {
        if (_mainWindow?.Content is not FrameworkElement root)
        {
            return;
        }

        EnsureTrayMenu();
        if (_trayMenu == null)
        {
            return;
        }

        ShowTrayHostWindowIfNeeded();
        if (root.XamlRoot == null)
        {
            return;
        }

        if (!WindowInterop.GetCursorPos(out var cursor))
        {
            return;
        }

        var maxY = GetWorkAreaBottom(cursor) - 10;
        if (cursor.Y > maxY)
        {
            cursor.Y = maxY;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        WindowInterop.ScreenToClient(hwnd, ref cursor);

        var scale = root.XamlRoot.RasterizationScale;
        var position = new Windows.Foundation.Point(cursor.X / scale, cursor.Y / scale);

        var options = new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            Position = position,
            ShowMode = FlyoutShowMode.Standard,
        };

        ApplyTrayMenuTopmost();
        _trayMenu.ShowAt(root, options);
    }

    private void ApplyTrayMenuTopmost()
    {
        if (_trayMenuTopmostApplied || _mainWindow == null)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        _trayMenuExStyle = WindowInterop.GetWindowLong(hwnd, WindowInterop.WlExStyle);
        _ = WindowInterop.SetWindowLong(
            hwnd,
            WindowInterop.WlExStyle,
            _trayMenuExStyle | WindowInterop.WsExTopmost
        );
        WindowInterop.SetWindowPos(
            hwnd,
            WindowInterop.HwndTopmost,
            0,
            0,
            0,
            0,
            WindowInterop.SwpNoMove | WindowInterop.SwpNoSize | WindowInterop.SwpNoActivate
        );
        _trayMenuTopmostApplied = true;
    }

    private void RestoreTrayMenuTopmost()
    {
        if (!_trayMenuTopmostApplied || _mainWindow == null)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        var wasTopmost = (_trayMenuExStyle & WindowInterop.WsExTopmost) != 0;
        var zOrder = wasTopmost ? WindowInterop.HwndTopmost : WindowInterop.HwndNoTopmost;

        WindowInterop.SetWindowPos(
            hwnd,
            zOrder,
            0,
            0,
            0,
            0,
            WindowInterop.SwpNoMove | WindowInterop.SwpNoSize | WindowInterop.SwpNoActivate
        );
        _ = WindowInterop.SetWindowLong(hwnd, WindowInterop.WlExStyle, _trayMenuExStyle);
        _trayMenuTopmostApplied = false;
    }

    private void ShowTrayHostWindowIfNeeded()
    {
        if (_mainWindow == null)
            return;

        if (_mainWindow.IsWindowVisible)
        {
            var currentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
            WindowInterop.SetForegroundWindow(currentHwnd);
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        _trayHostExStyle = WindowInterop.GetWindowLong(hwnd, WindowInterop.WlExStyle);
        _ = WindowInterop.SetWindowLong(
            hwnd,
            WindowInterop.WlExStyle,
            (_trayHostExStyle & ~WindowInterop.WsExAppWindow) | WindowInterop.WsExToolWindow | WindowInterop.WsExLayered
        );
        WindowInterop.SetLayeredWindowAttributes(hwnd, 0, 0, WindowInterop.LwaAlpha);

        _mainWindow.ShowAppWindow(); // Need to expose this
        WindowInterop.SetForegroundWindow(hwnd);
        _trayHostTemporarilyShown = true;
    }

    private void RestoreTrayHost(bool keepVisible)
    {
        if (_mainWindow == null)
            return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
        _ = WindowInterop.SetWindowLong(hwnd, WindowInterop.WlExStyle, _trayHostExStyle);
        WindowInterop.SetLayeredWindowAttributes(hwnd, 0, 255, WindowInterop.LwaAlpha);
        if (!keepVisible)
        {
            _mainWindow.HideAppWindow(); // Need to expose this
        }
        _trayHostTemporarilyShown = false;
    }

    private Task ToggleWindowOnUiAsync()
    {
        if (_mainWindow == null)
            return Task.CompletedTask;

        return RunOnUiThreadAsync(async () =>
        {
            if (_mainWindow.IsWindowVisible && !_trayHostTemporarilyShown)
            {
                await _mainWindow.HideToTrayAsync();
            }
            else
            {
                if (_trayHostTemporarilyShown)
                {
                    RestoreTrayHost(true);
                }

                await _mainWindow.AnimateWindowEntranceAsync();
                _mainWindow.EnsureWindowVisible();
                LogWindowShown();
            }
        });
    }

    private Task ShowWindowOnUiAsync()
    {
        if (_mainWindow == null)
            return Task.CompletedTask;

        return RunOnUiThreadAsync(async () =>
        {
            if (_mainWindow.IsWindowVisible && !_trayHostTemporarilyShown)
            {
                _mainWindow.EnsureWindowVisible();
                LogWindowShown();
                return;
            }

            if (_trayHostTemporarilyShown)
            {
                RestoreTrayHost(true);
            }

            await _mainWindow.AnimateWindowEntranceAsync();
            _mainWindow.EnsureWindowVisible();
            LogWindowShown();
        });
    }

    private void ShowWindow()
    {
        if (_mainWindow == null)
            return;

        if (_trayHostTemporarilyShown)
        {
            RestoreTrayHost(true);
        }

        _mainWindow.EnsureWindowVisible();
        LogWindowShown();
    }

    private async Task ExitFromTrayAsync()
    {
        if (_isProcessingTrayAction || _mainWindow == null)
            return;
        LogTrayExitRequested();

        bool serviceActive;
        try { serviceActive = await Task.Run(IsServiceActiveAsync).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex)
        {
            HotspotApp.Logging.ManualTestDiagnostics.Write("Saída: consulta do serviço falhou", ex);
            CloseFromTray();
            return;
        }
        if (serviceActive)
        {
            await RunOnUiThreadAsync(async () =>
            {
                ShowWindow();
                var choice = await _mainWindow.ShowExitConfirmationAsync();
                if (choice == TrayExitChoice.Cancel)
                {
                    return;
                }

                if (choice == TrayExitChoice.ExitAndDisableService)
                {
                    _isProcessingTrayAction = true;
                    _mainWindow.SetUiLocked(true);

                    try
                    {
                        await serviceStateManager.ApplyAsync(false);
                    }
                    catch (FileNotFoundException)
                    {
                        _mainWindow.ShowExecutableNotFound();
                        _mainWindow.SetUiLocked(false);
                        _isProcessingTrayAction = false;
                        errorHistoryService.AddError(
                            AppStrings.Get("Text_ExecutableNotFoundMessage")
                        );
                        return;
                    }
                    catch (Exception ex)
                    {
                        _mainWindow.ShowServiceError(ex, "TrayExitDisableServiceFailed");
                        _mainWindow.SetUiLocked(false);
                        _isProcessingTrayAction = false;
                        errorHistoryService.AddError(ex, "Exit");
                        return;
                    }
                }

                CloseFromTray();
            });
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            CloseFromTray();
            return Task.CompletedTask;
        });
    }

    private bool _closing;

    private async void CloseFromTray()
    {
        if (_closing) return;
        _closing = true;
        HotspotApp.Logging.ManualTestDiagnostics.Write("Saída solicitada: pedindo desligamento ao supervisor; espera da interface limitada a 15s");
        try
        {
            var session = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<HotspotApp.Services.Infrastructure.ManualHotspotSession>(App.Services);
            // O prazo inclui a aquisição do bloqueio e a chamada nativa inicial.
            // Não aguardar indefinidamente nem reiniciar a placa física como fallback.
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            HotspotApp.Logging.ManualTestDiagnostics.Write("Saída: teste parado e configuração restaurada");
        }
        catch (Exception ex)
        {
            HotspotApp.Logging.ManualTestDiagnostics.Write(
                "Saída: supervisor continua independente; confirmação ainda pendente, sem reiniciar adaptadores", ex);
        }
        finally
        {
            try { Dispose(); }
            finally { Application.Current.Exit(); }
        }
    }

    private void OpenLogs()
    {
        if (Directory.Exists(HotspotApp.Logging.LogPaths.DirectoryPath))
        {
            fileExplorerService.OpenFolder(HotspotApp.Logging.LogPaths.DirectoryPath);
        }
    }

    private async Task ShowQrFromTrayAsync()
    {
        if (_mainWindow == null)
            return;
        await RunOnUiThreadAsync(async () =>
        {
            try
            {
                ShowWindow();
                var config = await ConfigManager.LoadConfigAsync();
                await dialogService.ShowHotspotQrAsync(config.Ssid, config.Passphrase);
            }
            catch (Exception ex)
            {
                LogTrayQrShowFailed(ex);
                errorHistoryService.AddError(ex, "QR Code");
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WindowShown")]
    private partial void LogWindowShown();

    [LoggerMessage(Level = LogLevel.Information, Message = "TrayExitRequested")]
    private partial void LogTrayExitRequested();

    [LoggerMessage(Level = LogLevel.Error, Message = "TrayQrShowFailed")]
    private partial void LogTrayQrShowFailed(Exception ex);

    private async Task ToggleServiceFromTrayAsync()
    {
        if (_isProcessingTrayAction || _mainWindow == null)
            return;
        _isProcessingTrayAction = true;

        if (_mainWindow.IsWindowVisible && _mainWindow.Content is FrameworkElement)
        {
            _mainWindow.SetUiLocked(true);
        }

        try
        {
            var isInstalled = await serviceManager.IsServiceInstalledAsync(AppConstants.AppName);
            await serviceStateManager.ApplyAsync(!isInstalled);

            if (!isInstalled)
            {
                await serviceClient.WaitForHotspotActiveAsync();
            }

            // Notify Settings page to refresh if active
            _mainWindow.RefreshSettingsPage(); // Helper method on MainWindow
        }
        catch (FileNotFoundException)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (_trayHostTemporarilyShown)
                {
                    RestoreTrayHost(true);
                }
                return Task.CompletedTask;
            });
            _mainWindow.ShowExecutableNotFound();
            errorHistoryService.AddError(AppStrings.Get("Text_ExecutableNotFoundMessage"));
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (_trayHostTemporarilyShown)
                {
                    RestoreTrayHost(true);
                }
                return Task.CompletedTask;
            });
            _mainWindow.ShowServiceError(ex, "TrayToggleServiceFailed");
            errorHistoryService.AddError(ex, "Toggle Service");
        }
        finally
        {
            await UpdateTrayMenuStateAsync();
            if (_mainWindow.Content is FrameworkElement)
            {
                _mainWindow.SetUiLocked(false);
            }
            _isProcessingTrayAction = false;
        }
    }

    private async Task RestartHotspotFromTrayAsync()
    {
        if (_isProcessingTrayAction || _mainWindow == null)
            return;
        _isProcessingTrayAction = true;

        if (_mainWindow.IsWindowVisible && _mainWindow.Content is FrameworkElement)
        {
            _mainWindow.SetUiLocked(true);
        }

        try
        {
            var status = await serviceManager.GetServiceStatusAsync(AppConstants.AppName);
            if (status != ServiceStatus.Running)
            {
                return;
            }

            var response = await serviceClient.RestartHotspotAsync();
            if (!response.Success)
            {
                await RunOnUiThreadAsync(() =>
                    dialogService.ShowErrorAsync(
                        response.Error ?? AppStrings.Get("Text_ServiceErrorMessage")
                    )
                );
                errorHistoryService.AddError(response.Error ?? "Restart Failed");
            }
            else
            {
                await serviceClient.WaitForHotspotActiveAsync();
            }
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (_trayHostTemporarilyShown)
                {
                    RestoreTrayHost(true);
                }
                return Task.CompletedTask;
            });
            _mainWindow.ShowServiceError(ex, "TrayHotspotRestartFailed");
            errorHistoryService.AddError(ex, "Restart");
        }
        finally
        {
            await UpdateTrayMenuStateAsync();
            if (_mainWindow.Content is FrameworkElement)
            {
                _mainWindow.SetUiLocked(false);
            }
            _isProcessingTrayAction = false;
        }
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (_mainWindow == null)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (
            !_mainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
        )
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    private static unsafe int GetWorkAreaBottom(WindowInterop.Point cursor)
    {
        var monitor = WindowInterop.MonitorFromPoint(cursor, WindowInterop.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return cursor.Y;
        }

        var info = new WindowInterop.MonitorInfo
        {
            cbSize = (uint)sizeof(WindowInterop.MonitorInfo),
        };
        if (!WindowInterop.GetMonitorInfo(monitor, ref info))
        {
            return cursor.Y;
        }

        return info.rcWork.Bottom;
    }

    private async Task<bool> IsServiceActiveAsync()
    {
        var status = await serviceManager.GetServiceStatusAsync(AppConstants.AppName);
        return status == ServiceStatus.Running;
    }

    private static unsafe void CopyStringToFixedBuffer(
        string source,
        char* destination,
        int destinationSize
    )
    {
        if (string.IsNullOrEmpty(source))
        {
            destination[0] = '\0';
            return;
        }

        var sourceSpan = source.AsSpan();
        if (sourceSpan.Length >= destinationSize)
        {
            sourceSpan = sourceSpan[..(destinationSize - 1)];
        }

        sourceSpan.CopyTo(new Span<char>(destination, destinationSize));
        destination[sourceSpan.Length] = '\0';
    }
}
