using System.Runtime.InteropServices;
using System.Windows.Interop;
using static PeriBattery.Native;

namespace PeriBattery;

/// <summary>
/// Notification-area icon built on Shell_NotifyIcon (no WinForms dependency). It owns a hidden
/// top-level window that also receives TaskbarCreated and WM_DEVICECHANGE broadcasts.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private string _tooltip = "PeriBattery";
    private bool _added;

    public TrayIcon()
    {
        var parameters = new HwndSourceParameters("PeriBattery.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)WS_POPUP),
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        if (Environment.ProcessPath is string exe && ExtractIconExW(exe, 0, out IntPtr large, out IntPtr small, 1) > 0)
        {
            _icon = small != IntPtr.Zero ? small : large;
            if (small != IntPtr.Zero && large != IntPtr.Zero) DestroyIcon(large);
        }
        Add();
    }

    public event Action? Clicked;
    public event Action? RefreshRequested;
    public event Action? ExitRequested;

    /// <summary>Explorer restarted: the taskbar (and our icon) must be re-created.</summary>
    public event Action? TaskbarRecreated;

    /// <summary>A device node was added or removed somewhere in the system.</summary>
    public event Action? DevicesChanged;

    public void SetTooltip(string text)
    {
        if (text.Length > 127) text = text[..127];
        if (text == _tooltip) return;
        _tooltip = text;
        if (!_added) return;
        NOTIFYICONDATA data = NewData();
        data.uFlags = NIF_TIP | NIF_SHOWTIP;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = IconId,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        NOTIFYICONDATA data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_added) return;
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: the low word of lParam is the mouse/keyboard event.
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            if (evt == WM_LBUTTONUP) Clicked?.Invoke();
            else if (evt == WM_CONTEXTMENU || evt == WM_RBUTTONUP) ShowMenu();
            handled = true;
        }
        else if (msg == (int)_taskbarCreated)
        {
            Add();
            TaskbarRecreated?.Invoke();
        }
        else if (msg == WM_DEVICECHANGE && wParam.ToInt64() == DBT_DEVNODES_CHANGED)
        {
            DevicesChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MF_STRING, 1, "Abrir PeriBattery");
            AppendMenuW(menu, MF_STRING, 2, "Actualizar ahora");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, 3, "Salir");

            GetCursorPos(out POINT pt);
            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_source.Handle);
            int command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_BOTTOMALIGN, pt.X, pt.Y, _source.Handle, IntPtr.Zero);
            PostMessageW(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            switch (command)
            {
                case 1: Clicked?.Invoke(); break;
                case 2: RefreshRequested?.Invoke(); break;
                case 3: ExitRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            NOTIFYICONDATA data = NewData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        _source.Dispose();
    }
}
