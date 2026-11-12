using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PeriBattery.Core.Devices;
using PeriBattery.Core.Infra;

namespace PeriBattery;

public partial class App : Application
{
    private const string MutexName = @"Local\PeriBattery.SingleInstance";
    private const string ShowEventName = @"Local\PeriBattery.ShowFlyout";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private RollingFileLog? _log;
    private SettingsStore? _store;
    private AppSettings _settings = new();
    private DeviceManager? _manager;
    private MainVm? _vm;
    private readonly List<PillWindow> _pills = [];
    private DispatcherTimer? _pillSyncTimer;
    private FlyoutWindow? _flyout;
    private TrayIcon? _tray;
    private Toasts? _toasts;
    private DispatcherTimer? _saveTimer;
    private readonly LowBatteryTracker _lowBattery = new();
    private DateTime _flyoutClosedUtc;
    private string _knownSignature = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Troubleshooting aid: "PeriBattery.exe --test-toast" shows a sample notification and exits.
        if (e.Args.Contains("--test-toast"))
        {
            using var testLog = new RollingFileLog(Path.Combine(SettingsStore.LogFolder, "peribattery.log"));
            var toasts = new Toasts(testLog);
            toasts.Register();
            toasts.Show("PeriBattery", "Notificación de prueba: los avisos de batería baja funcionan.");
            Shutdown();
            return;
        }

        // Single instance: a second launch just asks the running one to open its flyout.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle? existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            Shutdown();
            return;
        }

        // Tiny transparent windows: software rendering avoids the GPU pipeline's memory overhead.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        _log = new RollingFileLog(Path.Combine(SettingsStore.LogFolder, "peribattery.log"));
        _store = new SettingsStore(_log);
        _settings = _store.Load();
        _log.DebugEnabled = _settings.DebugLog;
        _log.Info($"PeriBattery {typeof(App).Assembly.GetName().Version} starting (HID++ software ID 0x{_settings.HidppSoftwareId:X2})");

        DispatcherUnhandledException += (_, args) =>
        {
            _log.Warn("unhandled UI exception: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.Warn("unhandled exception: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Warn("unobserved task exception: " + args.Exception.Message);
            args.SetObserved();
        };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _store.Save(_settings);
        };

        _toasts = new Toasts(_log);
        _toasts.Register();

        _vm = new MainVm(_settings, QueueSave)
        {
            RefreshCommand = new RelayCommand(() => _manager?.RequestRefresh()),
            OpenLogsCommand = new RelayCommand(OpenLogFolder),
            QuitCommand = new RelayCommand(Quit),
        };
        _vm.DebugLogChanged += enabled => _log.DebugEnabled = enabled;

        IEnumerable<DeviceSnapshot> lastKnown = _settings.Known.Select(k =>
            new DeviceSnapshot(k.Id, k.Name, k.Kind, k.Link, Presence.Disconnected, k.Percent, ChargeState.Unknown, k.LastReadUtc));
        _manager = new DeviceManager(_log, (byte)_settings.HidppSoftwareId, lastKnown)
        {
            PollInterval = TimeSpan.FromSeconds(_settings.PollSeconds),
        };
        _vm.Apply(_manager.Current);
        _manager.Changed += list => Dispatcher.BeginInvoke(() => OnDevicesChanged(list));

        // Monitor hot-plug and Explorer restarts arrive as bursts of messages: re-sync the pills once they settle.
        _pillSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pillSyncTimer.Tick += (_, _) =>
        {
            _pillSyncTimer.Stop();
            SyncPills();
        };
        _vm.MonitorsSettingChanged += SyncPills;
        SyncPills();

        _tray = new TrayIcon();
        _tray.Clicked += () => ToggleFlyout(null);
        _tray.RefreshRequested += () => _manager.RequestRefresh();
        _tray.ExitRequested += Quit;
        _tray.TaskbarRecreated += QueuePillSync;
        // Plug/unplug anywhere in the system: re-scan shortly after the burst of notifications settles.
        _tray.DevicesChanged += () => _manager.RequestRefresh(1500);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() => ShowFlyout(null)), null, Timeout.Infinite, false);

        _manager.Start();
        TrimMemorySoon(12);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _manager?.RequestRefresh(4000);
    }

    private void OnDevicesChanged(IReadOnlyList<DeviceSnapshot> devices)
    {
        if (_vm == null || _tray == null) return;
        _vm.Apply(devices);
        _tray.SetTooltip(_vm.TooltipText);
        if (_manager != null) _vm.Diagnostics = string.Join("\n", _manager.Diagnostics);

        foreach (DeviceSnapshot d in devices)
        {
            if (!_lowBattery.ShouldNotify(d) || !_settings.LowBatteryNotifications) continue;
            string name = _settings.ForDevice(d.Id).CustomName ?? d.Name;
            _toasts?.Show("Batería baja", $"{name}: {d.Percent} % restante");
        }

        // Persist last known levels (only when something meaningful changed).
        string signature = string.Join("|", devices.Select(d => $"{d.Id}:{d.Percent}:{d.Name}:{d.Link}"));
        if (signature == _knownSignature) return;
        _knownSignature = signature;
        _settings.Known = devices.Select(d => new KnownDevice
        {
            Id = d.Id, Name = d.Name, Kind = d.Kind, Link = d.Link, Percent = d.Percent, LastReadUtc = d.LastReadUtc,
        }).ToList();
        QueueSave();
    }

    /// <summary>
    /// Startup and the flyout leave a lot of pages resident that the idle app never touches again.
    /// Hand them back to the OS a few seconds later so the resident footprint reflects what idle really needs.
    /// </summary>
    private void TrimMemorySoon(int seconds)
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_flyout != null) return;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Native.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        };
        timer.Start();
    }

    private void QueueSave()
    {
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private void QueuePillSync()
    {
        _pillSyncTimer?.Stop();
        _pillSyncTimer?.Start();
    }

    /// <summary>Keeps exactly one pill per taskbar: the primary one, plus each secondary monitor's when enabled.</summary>
    private void SyncPills()
    {
        if (_vm == null || _log == null) return;

        var wanted = new List<(IntPtr Taskbar, bool Primary)>();
        IntPtr primary = Native.FindWindowW("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) wanted.Add((primary, true));
        if (_settings.ShowOnAllMonitors)
        {
            IntPtr secondary = IntPtr.Zero;
            while ((secondary = Native.FindWindowExW(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                wanted.Add((secondary, false));
        }

        for (int i = _pills.Count - 1; i >= 0; i--)
        {
            PillWindow pill = _pills[i];
            if (Native.IsWindow(pill.Taskbar) && wanted.Any(w => w.Taskbar == pill.Taskbar)) continue;
            pill.Close();
            _pills.RemoveAt(i);
        }

        foreach ((IntPtr taskbar, bool isPrimary) in wanted)
        {
            if (_pills.Any(p => p.Taskbar == taskbar)) continue;
            var pill = new PillWindow(_vm, _log, taskbar, isPrimary);
            pill.Clicked += ToggleFlyout;
            pill.MenuRequested += () => _tray?.ShowMenu();
            pill.DisplayChanged += QueuePillSync;
            new WindowInteropHelper(pill).EnsureHandle();
            if (isPrimary) _pills.Insert(0, pill);
            else _pills.Add(pill);
        }
        _log.Debug($"pills: {_pills.Count} taskbar(s)");
    }

    private void ToggleFlyout(PillWindow? source)
    {
        if (_flyout != null)
        {
            _flyout.CloseOnce();
            return;
        }
        // Clicking the tray icon deactivates (and closes) the flyout first; do not reopen it immediately.
        if ((DateTime.UtcNow - _flyoutClosedUtc).TotalMilliseconds < 350) return;
        ShowFlyout(source);
    }

    private void ShowFlyout(PillWindow? source)
    {
        if (_vm == null) return;
        // Open next to the pill that was clicked; tray icon and second-instance launches use the primary one.
        Rect anchor = (source ?? _pills.FirstOrDefault())?.PillBounds ?? Rect.Empty;
        if (_flyout != null)
        {
            _flyout.Activate();
            return;
        }
        if (_manager != null) _vm.Diagnostics = string.Join("\n", _manager.Diagnostics);

        _flyout = new FlyoutWindow(_vm);
        _flyout.Closed += (_, _) =>
        {
            _flyout = null;
            _flyoutClosedUtc = DateTime.UtcNow;
            TrimMemorySoon(5);
        };
        _flyout.SourceInitialized += (_, _) => _flyout?.PlaceNear(anchor);
        _flyout.Show();
        _flyout.Activate();
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.LogFolder}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _log?.Warn("could not open the log folder: " + ex.Message);
        }
    }

    private void Quit()
    {
        _flyout?.CloseOnce();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        if (_saveTimer is { IsEnabled: true })
        {
            _saveTimer.Stop();
            _store?.Save(_settings);
        }
        _tray?.Dispose();
        _manager?.Dispose();
        _log?.Info("PeriBattery exiting");
        _log?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
