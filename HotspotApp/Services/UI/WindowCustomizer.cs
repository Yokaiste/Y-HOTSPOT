using HotspotApp.Helpers;
using System.Globalization;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace HotspotApp.Services.UI;

public sealed class WindowCustomizer
{
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private double? _savedHeight;
    private double _savedWidth = 1120;
    private bool _applyingSize;
    private Windows.Graphics.PointInt32? _draggedPosition;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private static readonly string SizePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Y-HOTSPOT", "window-size.txt");

    public WindowCustomizer(Window window, AppWindow appWindow)
    {
        _window = window;
        _appWindow = appWindow;
        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        try
        {
            var values = File.ReadAllLines(SizePath, Encoding.UTF8);
            if (values.Length == 2
                && double.TryParse(values[0], CultureInfo.InvariantCulture, out var width)
                && double.TryParse(values[1], CultureInfo.InvariantCulture, out var height)
                && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0)
            {
                _savedWidth = width;
                _savedHeight = height;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _saveTimer = _window.DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveSize();
    }

    public void InitializeWindowStyle()
    {
        var style = WindowInterop.GetWindowLong(_hWnd, WindowInterop.GwlStyle);
        style &= ~WindowInterop.WsCaption;
        style |= WindowInterop.WsThickFrame;
        _ = WindowInterop.SetWindowLong(_hWnd, WindowInterop.GwlStyle, style);

        var exStyle = WindowInterop.GetWindowLong(_hWnd, WindowInterop.WlExStyle);
        // A regular app window participates in the taskbar and Alt+Tab.
        exStyle &= ~(WindowInterop.WsExToolWindow | WindowInterop.WsExTopmost);
        exStyle |= WindowInterop.WsExAppWindow;
        _ = WindowInterop.SetWindowLong(_hWnd, WindowInterop.WlExStyle, exStyle);

        WindowInterop.SetWindowPos(
            _hWnd,
            IsPinned ? WindowInterop.HwndTopmost : WindowInterop.HwndNoTopmost,
            0,
            0,
            0,
            0,
            WindowInterop.SwpNoMove | WindowInterop.SwpNoSize | WindowInterop.SwpNoActivate
        );

        ApplyRoundedCorners();

        var presenter = _appWindow.Presenter as OverlappedPresenter ?? OverlappedPresenter.Create();
        presenter.IsResizable = true;
        presenter.IsMaximizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        _appWindow.SetPresenter(presenter);

        UpdateAppSize();
        _appWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange || _applyingSize || !_appWindow.IsVisible
                || presenter.State != OverlappedPresenterState.Restored) return;
            // Guardar em unidades lógicas para respeitar a escala de outros monitores.
            var scale = WindowInterop.GetDpiForWindow(_hWnd) / 96.0;
            _savedWidth = _appWindow.Size.Width / scale;
            _savedHeight = _appWindow.Size.Height / scale;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
    }

    public bool IsPinned { get; private set; }

    public void SetPinned(bool pinned)
    {
        // Showing the window again must preserve the user's explicit pin choice.
        if (!WindowInterop.SetWindowPos(_hWnd,
            pinned ? WindowInterop.HwndTopmost : WindowInterop.HwndNoTopmost,
            0, 0, 0, 0,
            WindowInterop.SwpNoMove | WindowInterop.SwpNoSize | WindowInterop.SwpNoActivate))
            throw new System.ComponentModel.Win32Exception();
        IsPinned = pinned;
    }

    public void RestorePosition()
    {
        // Reset both dimensions before moving, then persist the restored defaults.
        _saveTimer.Stop();
        _draggedPosition = null;
        _savedWidth = 1120;
        _savedHeight = null;
        UpdateAppSize();
        _appWindow.Move(GetBottomRightPosition());
        var scale = WindowInterop.GetDpiForWindow(_hWnd) / 96.0;
        _savedWidth = _appWindow.Size.Width / scale;
        _savedHeight = _appWindow.Size.Height / scale;
        SaveSize();
    }

    public void RememberPosition() => _draggedPosition = _appWindow.Position;

    private void SaveSize()
    {
        if (!_savedHeight.HasValue) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SizePath)!);
            File.WriteAllLines(SizePath, [
                _savedWidth.ToString(CultureInfo.InvariantCulture),
                _savedHeight.Value.ToString(CultureInfo.InvariantCulture)], new UTF8Encoding(false));
        }
        catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public void UpdateAppSize()
    {
        var dpi = WindowInterop.GetDpiForWindow(_hWnd);
        var scale = dpi / 96.0;
        var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        // Na primeira abertura, ocupar a altura útil sem encobrir a barra de tarefas.
        var width = Math.Clamp((int)(_savedWidth * scale), 1, workArea.Width);
        var height = _savedHeight.HasValue
            ? Math.Clamp((int)(_savedHeight.Value * scale), 1, workArea.Height)
            : workArea.Height;
        _applyingSize = true;
        try { _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height)); }
        finally { _applyingSize = false; }
    }

    public void UpdateAppPosition()
    {
        UpdateAppSize();
        var targetPos = _draggedPosition ?? GetBottomRightPosition();
        _appWindow.Move(targetPos);
    }

    public void EnsureWindowVisible()
    {
        var currentStyle = WindowInterop.GetWindowLong(_hWnd, WindowInterop.WlExStyle);
        if ((currentStyle & WindowInterop.WsExLayered) != 0)
        {
            _ = WindowInterop.SetWindowLong(
                _hWnd,
                WindowInterop.WlExStyle,
                currentStyle & ~WindowInterop.WsExLayered
            );
        }
        WindowInterop.SetLayeredWindowAttributes(_hWnd, 0, 255, WindowInterop.LwaAlpha);

        if (_draggedPosition.HasValue) _appWindow.Move(_draggedPosition.Value);
        else UpdateAppPosition();
        _appWindow.Show();

        WindowInterop.SetWindowPos(
            _hWnd,
            IsPinned ? WindowInterop.HwndTopmost : WindowInterop.HwndNoTopmost,
            0,
            0,
            0,
            0,
            WindowInterop.SwpNoMove | WindowInterop.SwpNoSize | WindowInterop.SwpNoActivate
        );

        _window.Activate();
        WindowInterop.SetForegroundWindow(_hWnd);
    }

    public async Task AnimateWindowEntranceAsync()
    {
        var targetPos = _draggedPosition ?? GetBottomRightPosition();
        var startY = targetPos.Y + 400;
        await AnimateWindowAsync(
            targetPos.X,
            startY,
            targetPos.Y,
            EaseOutCubic,
            static progress => progress,
            300,
            false
        );
    }

    public async Task AnimateWindowExitAsync()
    {
        SaveSize();
        var currentPos = _appWindow.Position;
        var targetY = currentPos.Y + 400;
        await AnimateWindowAsync(
            currentPos.X,
            currentPos.Y,
            targetY,
            EaseInCubic,
            static progress => 1 - progress,
            200,
            true
        );
    }

    public void InitializeEntrance()
    {
        var exStyle = WindowInterop.GetWindowLong(_hWnd, WindowInterop.WlExStyle);
        _ = WindowInterop.SetWindowLong(
            _hWnd,
            WindowInterop.WlExStyle,
            exStyle | WindowInterop.WsExLayered
        );
        WindowInterop.SetLayeredWindowAttributes(_hWnd, 0, 0, WindowInterop.LwaAlpha);

        var targetPos = _draggedPosition ?? GetBottomRightPosition();
        var startY = targetPos.Y + 400;
        _appWindow.Move(new Windows.Graphics.PointInt32(targetPos.X, startY));
    }

    private void ApplyRoundedCorners()
    {
        var preference = WindowInterop.DwmcpRound;
        WindowInterop.DwmSetWindowAttribute(
            _hWnd,
            WindowInterop.DwmwaWindowCornerPreference,
            ref preference,
            sizeof(int)
        );
    }

    private Windows.Graphics.PointInt32 GetBottomRightPosition()
    {
        var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var size = _appWindow.Size;

        var x = Math.Max(0, workArea.Width - size.Width - 10);
        var y = Math.Max(0, workArea.Height - size.Height - 10);

        return new Windows.Graphics.PointInt32(x + workArea.X, y + workArea.Y);
    }

    private Task<bool> AnimateWindowAsync(
        int x,
        int startY,
        int endY,
        Func<double, double> easing,
        Func<double, double> opacityMap,
        int duration,
        bool hideAfter
    )
    {
        var tcs = new TaskCompletionSource<bool>();
        var exStyle = WindowInterop.GetWindowLong(_hWnd, WindowInterop.WlExStyle);
        _ = WindowInterop.SetWindowLong(
            _hWnd,
            WindowInterop.WlExStyle,
            exStyle | WindowInterop.WsExLayered
        );

        var initialOpacity = (byte)(255 * opacityMap(easing(0)));
        WindowInterop.SetLayeredWindowAttributes(_hWnd, 0, initialOpacity, WindowInterop.LwaAlpha);

        _appWindow.Move(new Windows.Graphics.PointInt32(x, startY));

        if (!hideAfter && !_appWindow.IsVisible)
        {
            _appWindow.Show();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        void Cleanup()
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            stopwatch.Stop();

            if (hideAfter)
            {
                _appWindow.Hide();
            }

            // Restore style by removing the layered bit
            var currentStyle = WindowInterop.GetWindowLong(_hWnd, WindowInterop.WlExStyle);
            _ = WindowInterop.SetWindowLong(
                _hWnd,
                WindowInterop.WlExStyle,
                currentStyle & ~WindowInterop.WsExLayered
            );
            WindowInterop.SetLayeredWindowAttributes(_hWnd, 0, 255, WindowInterop.LwaAlpha);

            // Ensure final position is correct if not hiding
            if (!hideAfter)
            {
                _appWindow.Move(new Windows.Graphics.PointInt32(x, endY));
            }
        }

        void OnRendering(object? sender, object e)
        {
            try
            {
                if (!_appWindow.IsVisible && !hideAfter)
                {
                    // If window became invisible unexpectedly, abort
                    Cleanup();
                    tcs.TrySetResult(false);
                    return;
                }

                var elapsed = stopwatch.ElapsedMilliseconds;
                var progress = Math.Min(1.0, (double)elapsed / duration);

                var ease = easing(progress);
                var currentY = startY + (endY - startY) * ease;
                var opacity = (byte)(255 * opacityMap(ease));

                WindowInterop.SetLayeredWindowAttributes(_hWnd, 0, opacity, WindowInterop.LwaAlpha);
                _appWindow.Move(new Windows.Graphics.PointInt32(x, (int)currentY));

                if (progress >= 1.0)
                {
                    Cleanup();
                    tcs.TrySetResult(true);
                }
            }
            catch (Exception ex)
            {
                Cleanup();
                tcs.TrySetException(ex);
            }
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

        return tcs.Task;
    }

    private static double EaseOutCubic(double t)
    {
        return 1 - Math.Pow(1 - t, 3);
    }

    private static double EaseInCubic(double t)
    {
        return t * t * t;
    }
}
