using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PeriBattery.Core.Devices;

namespace PeriBattery;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// <summary>One device row, shared by the pill and the flyout.</summary>
public sealed class DeviceVm : Observable
{
    private const double MaxFillWidth = 18;

    private static readonly Brush Green = Frozen("#2ECC8F");
    private static readonly Brush Orange = Frozen("#F5A623");
    private static readonly Brush Red = Frozen("#FF4D4F");
    private static readonly Brush Grey = Frozen("#8A7A7C");

    private static readonly Dictionary<DeviceKind, Geometry> Icons = new()
    {
        [DeviceKind.Keyboard] = Icon("M3,6 H21 A2,2 0 0 1 23,8 V17 A2,2 0 0 1 21,19 H3 A2,2 0 0 1 1,17 V8 A2,2 0 0 1 3,6 Z M5,10 H6 M9,10 H10 M13,10 H14 M17,10 H18 M5,13 H6 M9,13 H10 M13,13 H14 M17,13 H18 M8,16 H16"),
        [DeviceKind.Mouse] = Icon("M12,2 A6,6 0 0 1 18,8 V16 A6,6 0 0 1 6,16 V8 A6,6 0 0 1 12,2 Z M12,6 V10"),
        [DeviceKind.Headset] = Icon("M4,15 V12 A8,8 0 0 1 20,12 V15 M4,14 A2,2 0 0 1 6,12 H7 V19 H6 A2,2 0 0 1 4,17 Z M20,14 A2,2 0 0 0 18,12 H17 V19 H18 A2,2 0 0 0 20,17 Z"),
        [DeviceKind.Gamepad] = Icon("M7,7 H17 A5,5 0 0 1 22,12 L23,17 A2.4,2.4 0 0 1 18.6,18.6 L16.5,16 H7.5 L5.4,18.6 A2.4,2.4 0 0 1 1,17 L2,12 A5,5 0 0 1 7,7 Z M8,10 V13 M6.5,11.5 H9.5 M15.5,10.5 H15.6 M17.5,12.5 H17.6"),
        [DeviceKind.Other] = Icon("M8,7 L16,15 L12,19 V5 L16,9 L8,17"),
    };

    private readonly AppSettings _settings;
    private readonly Action _settingsChanged;
    private DeviceSnapshot _snapshot;

    public DeviceVm(DeviceSnapshot snapshot, AppSettings settings, Action settingsChanged)
    {
        _snapshot = snapshot;
        _settings = settings;
        _settingsChanged = settingsChanged;
    }

    public string Id => _snapshot.Id;
    public DeviceSnapshot Snapshot => _snapshot;
    public Geometry IconGeometry => Icons[_snapshot.Kind];

    public string DisplayName
    {
        get => _settings.ForDevice(Id).CustomName ?? _snapshot.Name;
        set
        {
            string trimmed = (value ?? "").Trim();
            _settings.ForDevice(Id).CustomName = trimmed.Length == 0 || trimmed == _snapshot.Name ? null : trimmed;
            _settingsChanged();
            Raise();
        }
    }

    public bool ShowInPill
    {
        get => _settings.ForDevice(Id).ShowInPill;
        set
        {
            _settings.ForDevice(Id).ShowInPill = value;
            _settingsChanged();
            Raise();
        }
    }

    public string PercentText => _snapshot.Percent is int p ? $"{p}%" : "—";
    public double FillWidth => _snapshot.Percent is int p ? Math.Max(2, MaxFillWidth * Math.Clamp(p, 0, 100) / 100.0) : 0;
    public bool IsCharging => _snapshot.IsCharging;
    public bool IsStale => _snapshot.IsStale;
    public double ContentOpacity => _snapshot.IsStale ? 0.45 : 1.0;
    public bool IsDisconnected => _snapshot.Presence == Presence.Disconnected;

    public Brush LevelBrush => _snapshot.Percent switch
    {
        null => Grey,
        _ when _snapshot.Presence == Presence.Disconnected => Grey,
        > 50 => Green,
        >= 10 => Orange,
        _ => Red,
    };

    public string StatusText
    {
        get
        {
            string last = _snapshot.Percent is int p ? $" · último nivel {p} %" : "";
            // The level is already shown on the right for sleeping devices; keep that line short.
            string asleep = _snapshot.Percent is int ? " · último nivel conocido" : "";
            string text = _snapshot.Presence switch
            {
                Presence.Connected => $"Conectado · {_snapshot.Link}" + (_snapshot.Charge switch
                {
                    ChargeState.Charging => " · cargando",
                    ChargeState.Full => " · carga completa",
                    _ => "",
                }),
                Presence.Asleep => $"En reposo · {_snapshot.Link}{asleep}",
                _ => $"Desconectado{last}",
            };
            return _snapshot.Unreliable ? text + " · lectura poco fiable" : text;
        }
    }

    public void Update(DeviceSnapshot snapshot)
    {
        if (snapshot == _snapshot) return;
        _snapshot = snapshot;
        Raise(string.Empty); // refresh every binding
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Geometry Icon(string data)
    {
        Geometry g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }
}

/// <summary>One secondary monitor's row in the Design tab.</summary>
public sealed class MonitorVm : Observable
{
    private readonly MonitorSettings _settings;
    private readonly Action _changed;

    public MonitorVm(string key, string label, MonitorSettings settings, Action changed)
    {
        Key = key;
        Label = label;
        _settings = settings;
        _changed = changed;
        ToggleSideCommand = new RelayCommand(() =>
        {
            _settings.AlignLeft = !(_settings.AlignLeft ?? false);
            Raise(nameof(SideText));
            _changed();
        });
    }

    public string Key { get; }
    public string Label { get; }
    public ICommand ToggleSideCommand { get; }
    public string SideText => _settings.AlignLeft == true ? "Izquierda" : "Derecha";

    public bool Show
    {
        get => _settings.Show;
        set { _settings.Show = value; Raise(); _changed(); }
    }
}

public sealed class MainVm : Observable
{
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Dictionary<string, DeviceVm> _byId = [];

    public MainVm(AppSettings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        ApplyAccent();
    }

    public ObservableCollection<DeviceVm> PillDevices { get; } = [];
    public ObservableCollection<DeviceVm> ConnectedDevices { get; } = [];
    public ObservableCollection<DeviceVm> DisconnectedDevices { get; } = [];

    public string[] AccentPresets { get; } = ["#FF6B6B", "#FF8A3D", "#F5C542", "#2ECC8F", "#3FA9F5", "#9B7BFF", "#FF6FB5", "#C8C8C8"];

    public ICommand? RefreshCommand { get; set; }
    public ICommand? OpenLogsCommand { get; set; }
    public ICommand? QuitCommand { get; set; }

    /// <summary>Raised when something that affects the pill's size or visibility changed.</summary>
    public event Action? PillLayoutChanged;
    public event Action<bool>? DebugLogChanged;

    public string Subtitle
    {
        get
        {
            int n = _byId.Values.Count(d => d.Snapshot.Presence == Presence.Connected);
            return n == 1 ? "1 dispositivo conectado" : $"{n} dispositivos conectados";
        }
    }

    public bool HasDisconnected => DisconnectedDevices.Count > 0;
    public string DisconnectedHeader => $"Desconectados ({DisconnectedDevices.Count})";
    public bool HasNoDevices => _byId.Count == 0;

    public string TooltipText
    {
        get
        {
            var parts = _byId.Values.Where(d => !d.IsDisconnected).Select(d => $"{d.DisplayName} {d.PercentText}");
            string joined = string.Join(" · ", parts);
            return joined.Length == 0 ? "PeriBattery" : "PeriBattery — " + joined;
        }
    }

    public string Version => "Versión " + (typeof(MainVm).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    private string _diagnostics = "";
    public string Diagnostics
    {
        get => _diagnostics;
        set { _diagnostics = value; Raise(); }
    }

    public double PillScale
    {
        get => _settings.PillScale;
        set { _settings.PillScale = Math.Round(value, 2); Changed(); PillLayoutChanged?.Invoke(); }
    }

    public double PillOpacity
    {
        get => _settings.PillOpacity;
        set { _settings.PillOpacity = Math.Round(value, 2); Changed(); }
    }

    public bool ShowPercentText
    {
        get => _settings.ShowPercentText;
        set { _settings.ShowPercentText = value; Changed(); PillLayoutChanged?.Invoke(); }
    }

    /// <summary>Raised when the set of taskbars that should carry a pill changed.</summary>
    public event Action? MonitorsSettingChanged;

    public ObservableCollection<MonitorVm> SecondaryMonitors { get; } = [];
    public bool HasSecondaryMonitors => SecondaryMonitors.Count > 0;

    public double SecondaryPillScale
    {
        get => _settings.SecondaryPillScale;
        set { _settings.SecondaryPillScale = Math.Round(value, 2); Changed(); PillLayoutChanged?.Invoke(); }
    }

    /// <summary>Updates the list of secondary monitors shown in the Design tab (called when taskbars are re-synced).</summary>
    public void SetSecondaryMonitors(IReadOnlyList<(string Key, string Label)> monitors)
    {
        if (SecondaryMonitors.Select(m => (m.Key, m.Label)).SequenceEqual(monitors)) return;
        SecondaryMonitors.Clear();
        foreach ((string key, string label) in monitors)
        {
            SecondaryMonitors.Add(new MonitorVm(key, label, _settings.Monitors[key], () =>
            {
                _saveSettings();
                MonitorsSettingChanged?.Invoke();
            }));
        }
        Raise(nameof(HasSecondaryMonitors));
    }

    public string AccentColor
    {
        get => _settings.AccentColor;
        set { _settings.AccentColor = value; ApplyAccent(); Changed(); }
    }

    public bool LowBatteryNotifications
    {
        get => _settings.LowBatteryNotifications;
        set { _settings.LowBatteryNotifications = value; Changed(); }
    }

    public bool HideInFullscreen
    {
        get => _settings.HideInFullscreen;
        set { _settings.HideInFullscreen = value; Changed(); PillLayoutChanged?.Invoke(); }
    }

    public bool DebugLog
    {
        get => _settings.DebugLog;
        set { _settings.DebugLog = value; Changed(); DebugLogChanged?.Invoke(value); }
    }

    public bool StartWithWindows
    {
        get => Autostart.IsEnabled;
        set
        {
            try { Autostart.Set(value); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }
            Raise();
        }
    }

    private void Changed([CallerMemberName] string? name = null)
    {
        _saveSettings();
        Raise(name);
    }

    private void ApplyAccent()
    {
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(_settings.AccentColor); }
        catch (FormatException) { color = Color.FromRgb(0xFF, 0x6B, 0x6B); }

        ResourceDictionary res = Application.Current.Resources;
        res["AccentBrush"] = Freeze(new SolidColorBrush(color));
        res["AccentBorderBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, color.R, color.G, color.B)));
        res["AccentSoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B)));
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Applies a new device list coming from the device manager (UI thread).</summary>
    public void Apply(IReadOnlyList<DeviceSnapshot> devices)
    {
        var seen = new HashSet<string>();
        foreach (DeviceSnapshot s in devices)
        {
            seen.Add(s.Id);
            if (_byId.TryGetValue(s.Id, out DeviceVm? vm)) vm.Update(s);
            else _byId[s.Id] = new DeviceVm(s, _settings, OnDeviceSettingChanged);
        }
        foreach (string id in _byId.Keys.Where(k => !seen.Contains(k)).ToList()) _byId.Remove(id);

        List<DeviceVm> ordered = devices.Select(d => _byId[d.Id]).ToList();
        Sync(ConnectedDevices, ordered.Where(d => !d.IsDisconnected));
        Sync(DisconnectedDevices, ordered.Where(d => d.IsDisconnected));
        bool pillChanged = Sync(PillDevices, ordered.Where(d => !d.IsDisconnected && d.ShowInPill));

        Raise(nameof(Subtitle));
        Raise(nameof(HasDisconnected));
        Raise(nameof(DisconnectedHeader));
        Raise(nameof(HasNoDevices));
        Raise(nameof(TooltipText));
        if (pillChanged) PillLayoutChanged?.Invoke();
    }

    private void OnDeviceSettingChanged()
    {
        _saveSettings();
        List<DeviceVm> ordered = [.. ConnectedDevices];
        if (Sync(PillDevices, ordered.Where(d => d.ShowInPill))) PillLayoutChanged?.Invoke();
        Raise(nameof(TooltipText));
    }

    private static bool Sync(ObservableCollection<DeviceVm> target, IEnumerable<DeviceVm> source)
    {
        List<DeviceVm> wanted = source.ToList();
        if (target.SequenceEqual(wanted)) return false;
        target.Clear();
        foreach (DeviceVm d in wanted) target.Add(d);
        return true;
    }
}
