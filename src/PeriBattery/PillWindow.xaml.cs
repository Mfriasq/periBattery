using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PeriBattery.Core.Infra;
using static PeriBattery.Native;

namespace PeriBattery;

/// <summary>
/// The taskbar overlay. A transparent, never-activating tool window kept topmost and anchored to the
/// left of the notification area. It follows the taskbar through WinEvent hooks and display/DPI
/// messages, and hides while a fullscreen application is in the foreground.
/// </summary>
public partial class PillWindow : Window
{
    private readonly MainVm _vm;
    private readonly ILog _log;
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _repositionDebounce;
    private readonly WinEventProc _foregroundProc;
    private readonly WinEventProc _taskbarProc;
    private IntPtr _hwnd;
    private IntPtr _foregroundHook;
    private IntPtr _taskbarHook;
    private IntPtr _taskbar;
    private bool _fullscreen;
    private bool _visible;

    public PillWindow(MainVm vm, ILog log)
    {
        InitializeComponent();
        _vm = vm;
        _log = log;
        DataContext = vm;

        _foregroundProc = OnForegroundEvent;
        _taskbarProc = OnTaskbarEvent;

        // Low-frequency safety net: re-asserts z-order above the taskbar and catches windows that
        // go fullscreen without a foreground change. Costs nothing measurable at this interval.
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _watchdog.Tick += (_, _) => Evaluate();

        _repositionDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _repositionDebounce.Tick += (_, _) =>
        {
            _repositionDebounce.Stop();
            Reposition();
        };

        vm.PillLayoutChanged += () => Dispatcher.BeginInvoke(Evaluate, DispatcherPriority.Loaded);
        SizeChanged += (_, _) => Reposition();
    }

    public event Action? Clicked;
    public event Action? MenuRequested;

    /// <summary>Screen rectangle of the pill in physical pixels (empty when hidden).</summary>
    public Rect PillBounds
    {
        get
        {
            if (!_visible || !GetWindowRect(_hwnd, out RECT r)) return Rect.Empty;
            return new Rect(r.Left, r.Top, r.Width, r.Height);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: no taskbar button, no Alt+Tab entry. No-activate: clicking never steals focus (e.g. from a game).
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        HookTaskbar();
        _watchdog.Start();
        Evaluate();
    }

    /// <summary>Hooks location changes of the taskbar window only (not system-wide), so the pill follows it.</summary>
    public void HookTaskbar()
    {
        if (_taskbarHook != IntPtr.Zero)
        {
            UnhookWinEvent(_taskbarHook);
            _taskbarHook = IntPtr.Zero;
        }
        _taskbar = FindWindowW("Shell_TrayWnd", null);
        if (_taskbar == IntPtr.Zero) return;
        uint thread = GetWindowThreadProcessId(_taskbar, out uint pid);
        _taskbarHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _taskbarProc, pid, thread, WINEVENT_OUTOFCONTEXT);
        QueueReposition();
    }

    private void OnForegroundEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Called from native code: an exception must never propagate back into the hook dispatcher.
        try { Evaluate(); }
        catch (Exception ex) { _log.Warn("pill: evaluate failed: " + ex.Message); }
    }

    private void OnTaskbarEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // idObject 0 = the window itself (ignores caret/cursor objects).
        if (idObject == 0 && hwnd != IntPtr.Zero) QueueReposition();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE:
            case WM_DPICHANGED:
                QueueReposition();
                break;
        }
        return IntPtr.Zero;
    }

    private void QueueReposition()
    {
        _repositionDebounce.Stop();
        _repositionDebounce.Start();
    }

    /// <summary>Recomputes visibility (devices to show, fullscreen app) and keeps the pill above the taskbar.</summary>
    public void Evaluate()
    {
        if (_hwnd == IntPtr.Zero) return;

        bool fullscreen = false;
        if (_vm.HideInFullscreen)
        {
            IntPtr monitor = _taskbar != IntPtr.Zero ? MonitorFromWindow(_taskbar, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
            fullscreen = FullscreenDetector.IsFullscreenAppActive(_taskbar, monitor);
        }
        if (fullscreen != _fullscreen)
        {
            _fullscreen = fullscreen;
            _log.Info($"pill: fullscreen app {(fullscreen ? "detected, hiding" : "gone, showing")} (QUNS={FullscreenDetector.NotificationState()})");
        }

        bool shouldShow = !fullscreen && _vm.PillDevices.Count > 0;
        if (shouldShow != _visible)
        {
            _visible = shouldShow;
            if (shouldShow) Show();
            else Hide();
        }
        if (_visible) Reposition();
    }

    private void Reposition()
    {
        if (!_visible || _hwnd == IntPtr.Zero) return;
        if (_taskbar == IntPtr.Zero || !GetWindowRect(_taskbar, out RECT bar))
        {
            _taskbar = FindWindowW("Shell_TrayWnd", null);
            if (_taskbar == IntPtr.Zero || !GetWindowRect(_taskbar, out bar)) return;
        }
        if (!GetWindowRect(_hwnd, out RECT me) || me.Width <= 0) return;

        double scale = GetDpiForWindow(_taskbar) / 96.0;
        int anchorRight = bar.Right - (int)(300 * scale);
        IntPtr tray = FindWindowExW(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out RECT trayRect) && trayRect.Width > 0 && trayRect.Left > bar.Left)
            anchorRight = trayRect.Left;

        int x = Math.Max(bar.Left, anchorRight - me.Width - (int)(6 * scale));
        int y = bar.Top + (bar.Height - me.Height) / 2;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void OnLeftClick(object sender, MouseButtonEventArgs e) => Clicked?.Invoke();

    private void OnRightClick(object sender, MouseButtonEventArgs e) => MenuRequested?.Invoke();

    protected override void OnClosed(EventArgs e)
    {
        _watchdog.Stop();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_taskbarHook != IntPtr.Zero) UnhookWinEvent(_taskbarHook);
        base.OnClosed(e);
    }
}
