using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
        Rect work = SystemParameters.WorkArea; // primary monitor, device-independent units
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        double left = work.Right - Width - 4;
        if (!anchor.IsEmpty)
        {
            double centre = (anchor.Left + anchor.Width / 2) / scale;
            left = centre - Width / 2;
        }
        Left = Math.Max(work.Left + 4, Math.Min(left, work.Right - Width - 4));
        Top = Math.Max(work.Top + 4, work.Bottom - Height - 2);
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
