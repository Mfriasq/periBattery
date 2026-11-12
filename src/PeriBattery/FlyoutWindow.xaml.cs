using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace PeriBattery;

/// <summary>Settings/device flyout. Created on demand and released when closed to keep the idle footprint small.</summary>
public partial class FlyoutWindow : Window
{
    private readonly MainVm _vm;
    private bool _closing;

    public FlyoutWindow(MainVm vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>Places the flyout above the taskbar, centred on the pill when possible.</summary>
    /// <param name="anchor">Pill rectangle in physical pixels, or <see cref="Rect.Empty"/>.</param>
    public void PlaceNear(Rect anchor)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Work in physical pixels: monitors can have different scale factors.
        IntPtr monitor = anchor.IsEmpty
            ? Native.MonitorFromWindow(Native.FindWindowW("Shell_TrayWnd", null), Native.MONITOR_DEFAULTTONEAREST)
            : Native.MonitorFromPoint(new Native.POINT { X = (int)(anchor.Left + anchor.Width / 2), Y = (int)(anchor.Top + anchor.Height / 2) }, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfoW(monitor, ref info)) return;
        Native.RECT work = info.rcWork;

        // Step 1: move onto the target monitor so the window takes that monitor's DPI (and pixel size).
        Native.SetWindowPos(hwnd, IntPtr.Zero, work.Left + 8, work.Top + 8, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);

        // Step 2: once resized for that DPI, dock it above the taskbar, centred on the pill.
        void Dock()
        {
            if (!Native.GetWindowRect(hwnd, out Native.RECT me)) return;
            int x = work.Right - me.Width - 4;
            if (!anchor.IsEmpty) x = (int)(anchor.Left + anchor.Width / 2) - me.Width / 2;
            x = Math.Max(work.Left + 4, Math.Min(x, work.Right - me.Width - 4));
            int y = Math.Max(work.Top + 4, work.Bottom - me.Height - 2);
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }
        Dock();
        Dispatcher.BeginInvoke(Dock, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnDeactivated(object? sender, EventArgs e) => CloseOnce();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseOnce();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseOnce();
        }
        else if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl+Tab / Ctrl+Shift+Tab cycle through the tabs wherever the focus is.
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? Tabs.Items.Count - 1 : 1;
            Tabs.SelectedIndex = (Tabs.SelectedIndex + step) % Tabs.Items.Count;
            e.Handled = true;
        }
    }

    public void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        // Commit a rename in progress before the window goes away.
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Close();
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void OnSwatchLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string color)
            rb.IsChecked = string.Equals(color, _vm.AccentColor, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSwatchChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string color } && !string.Equals(color, _vm.AccentColor, StringComparison.OrdinalIgnoreCase))
            _vm.AccentColor = color;
    }
}
