using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PeriBattery.Core.Infra;
using static PeriBattery.Native;

namespace PeriBattery;

/// <summary>
/// The taskbar overlay for one taskbar (the primary one or a secondary monitor's). A transparent,
/// never-activating tool window kept topmost and anchored next to the notification area (primary) or
/// the clock (secondary). It follows its taskbar through WinEvent hooks and display/DPI messages, and
/// hides while a fullscreen application covers that monitor.
/// </summary>
public partial class PillWindow : Window
{
    /// <summary>Space kept free at the right end of a secondary taskbar (clock and notification bell), in DIPs.</summary>
    private const double SecondaryRightReserve = 150;

    private readonly MainVm _vm;
    private readonly ILog _log;
    private readonly IntPtr _taskbar;
    private readonly bool _primary;
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _repositionDebounce;
    private readonly WinEventProc _foregroundProc;
    private readonly WinEventProc _taskbarProc;
    private readonly Action _layoutChanged;
    private IntPtr _hwnd;
    private IntPtr _foregroundHook;
    private IntPtr _taskbarHook;
    private bool _fullscreen;
    private bool _visible;
    private bool _closed;

    public PillWindow(MainVm vm, ILog log, IntPtr taskbar, bool primary, bool alignLeft)
    {
        InitializeComponent();
        _vm = vm;
        _log = log;
        _taskbar = taskbar;
        _primary = primary;
        AlignLeft = alignLeft;
        DataContext = vm;

        if (!primary)
        {
            // Secondary taskbars use their own size setting.
            var binding = new System.Windows.Data.Binding(nameof(MainVm.SecondaryPillScale));
            System.Windows.Data.BindingOperations.SetBinding(Scale, System.Windows.Media.ScaleTransform.ScaleXProperty, binding);
            System.Windows.Data.BindingOperations.SetBinding(Scale, System.Windows.Media.ScaleTransform.ScaleYProperty, binding);
        }

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

        _layoutChanged = () => Dispatcher.BeginInvoke(Evaluate, DispatcherPriority.Loaded);
        vm.PillLayoutChanged += _layoutChanged;
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Taskbar window this pill is attached to.</summary>
    public IntPtr Taskbar => _taskbar;

    /// <summary>Anchored to the far left of the taskbar instead of next to the tray/clock.</summary>
    public bool AlignLeft { get; }

    public event Action<PillWindow>? Clicked;
    public event Action? MenuRequested;

    /// <summary>Monitors were added, removed or rearranged: the set of taskbars may have changed.</summary>
    public event Action? DisplayChanged;

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

        // Location changes of this taskbar's thread only (not system-wide), so the pill follows it.
        uint thread = GetWindowThreadProcessId(_taskbar, out uint pid);
        if (thread != 0)
            _taskbarHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _taskbarProc, pid, thread, WINEVENT_OUTOFCONTEXT);

        _watchdog.Start();
        Evaluate();
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
        if (idObject == 0 && hwnd == _taskbar) QueueReposition();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_DISPLAYCHANGE:
                QueueReposition();
                DisplayChanged?.Invoke();
                break;
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
        if (_hwnd == IntPtr.Zero || _closed) return;

        bool fullscreen = false;
        if (_vm.HideInFullscreen)
        {
            IntPtr monitor = MonitorFromWindow(_taskbar, MONITOR_DEFAULTTONEAREST);
            fullscreen = FullscreenDetector.IsFullscreenAppActive(_taskbar, monitor);
        }
        if (fullscreen != _fullscreen)
        {
            _fullscreen = fullscreen;
            _log.Info($"pill ({(_primary ? "primary" : "secondary")} taskbar): fullscreen app {(fullscreen ? "detected, hiding" : "gone, showing")} (QUNS={FullscreenDetector.NotificationState()})");
        }

        bool shouldShow = !fullscreen && _vm.PillDevices.Count > 0 && IsWindowVisible(_taskbar);
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
        if (!_visible || _hwnd == IntPtr.Zero || _closed) return;
        if (!GetWindowRect(_taskbar, out RECT bar) || bar.Width <= 0) return;
        if (!GetWindowRect(_hwnd, out RECT me) || me.Width <= 0) return;

        double scale = GetDpiForWindow(_taskbar) / 96.0;
        int anchorRight;
        if (_primary)
        {
            anchorRight = bar.Right - (int)(300 * scale);
            IntPtr tray = FindWindowExW(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero && GetWindowRect(tray, out RECT trayRect) && trayRect.Width > 0 && trayRect.Left > bar.Left)
                anchorRight = trayRect.Left;
        }
        else
        {
            // Secondary taskbars have no notification-area window: stay clear of the clock at the right end.
            anchorRight = bar.Right - (int)(SecondaryRightReserve * scale);
        }

        int x = AlignLeft
            ? bar.Left + (int)(6 * scale)
            : Math.Max(bar.Left, anchorRight - me.Width - (int)(6 * scale));
        int y = bar.Top + (bar.Height - me.Height) / 2;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void OnLeftClick(object sender, MouseButtonEventArgs e) => Clicked?.Invoke(this);

    private void OnRightClick(object sender, MouseButtonEventArgs e) => MenuRequested?.Invoke();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _watchdog.Stop();
        _repositionDebounce.Stop();
        _vm.PillLayoutChanged -= _layoutChanged;
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_taskbarHook != IntPtr.Zero) UnhookWinEvent(_taskbarHook);
        base.OnClosed(e);
    }
}
