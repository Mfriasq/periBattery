using System.Runtime.InteropServices;
using static PeriBattery.Native;

namespace PeriBattery;

/// <summary>
/// Detects exclusive-fullscreen and borderless-fullscreen applications on the pill's monitor.
/// Three signals are combined:
///  1. SHQueryUserNotificationState reporting Direct3D exclusive fullscreen or presentation mode;
///  2. the foreground window covering its whole monitor;
///  3. any window covering the monitor while sitting above the taskbar in Z-order. This catches a
///     borderless game that keeps the taskbar hidden even though another monitor has the focus.
/// Desktop, shell and click-through overlay windows are ignored.
/// </summary>
internal static class FullscreenDetector
{
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "ForegroundStaging", "LockScreenControllerProxyWindow",
        "Shell_InputSwitchTopLevelWindow", "ControlCenterWindow", "Xaml_WindowedPopupClass", "ApplicationFrameWindow",
    };

    /// <param name="taskbar">Primary taskbar window (Shell_TrayWnd).</param>
    /// <param name="pillMonitor">Monitor that hosts the pill; fullscreen windows on other monitors are ignored.</param>
    public static bool IsFullscreenAppActive(IntPtr taskbar, IntPtr pillMonitor)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != GetShellWindow() && IsCandidate(fg, allowOverlays: true))
        {
            bool sameMonitor = pillMonitor == IntPtr.Zero || MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) == pillMonitor;
            if (sameMonitor)
            {
                if (SHQueryUserNotificationState(out int state) == 0 && state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
                    return true;
                if (CoversMonitor(fg)) return true;
            }
        }

        return taskbar != IntPtr.Zero && IsTaskbarCovered(taskbar, pillMonitor);
    }

    /// <summary>Walks top-level windows from the top of the Z-order down to the taskbar looking for one that covers the monitor.</summary>
    private static bool IsTaskbarCovered(IntPtr taskbar, IntPtr pillMonitor)
    {
        bool covered = false;
        EnumWindowsProc callback = (hwnd, _) =>
        {
            if (hwnd == taskbar) return false; // reached the taskbar: nothing above it is fullscreen
            if (!IsCandidate(hwnd, allowOverlays: false)) return true;
            if (pillMonitor != IntPtr.Zero && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != pillMonitor) return true;
            if (!CoversMonitor(hwnd)) return true;
            covered = true;
            return false;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return covered;
    }

    private static bool IsCandidate(IntPtr hwnd, bool allowOverlays)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        if (!allowOverlays)
        {
            // GPU/chat overlays are fullscreen, click-through, never-activated tool windows: not applications.
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) != 0) return false;
        }

        if (ShellClasses.Contains(GetClassName(hwnd))) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return true;
    }

    private static bool CoversMonitor(IntPtr hwnd)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(hwnd, out RECT rect)) return false;

        bool covers = rect.Left <= info.rcMonitor.Left && rect.Top <= info.rcMonitor.Top &&
                      rect.Right >= info.rcMonitor.Right && rect.Bottom >= info.rcMonitor.Bottom;
        if (!covers) return false;

        // A regular maximized window (caption + maximized) only covers the monitor when the taskbar auto-hides: not fullscreen.
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        return !((style & WS_MAXIMIZE) != 0 && (style & WS_CAPTION) == WS_CAPTION);
    }

    /// <summary>Raw shell notification state, for the debug log.</summary>
    public static int NotificationState() => SHQueryUserNotificationState(out int state) == 0 ? state : -1;
}
