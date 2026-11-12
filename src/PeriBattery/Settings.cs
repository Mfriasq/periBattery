using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using PeriBattery.Core.Devices;
using PeriBattery.Core.Infra;

namespace PeriBattery;

public sealed class DeviceSettings
{
    public bool ShowInPill { get; set; } = true;
    public string? CustomName { get; set; }
}

/// <summary>Last known state of a device, so it can be listed with its last level while disconnected.</summary>
public sealed class KnownDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; }
    public string Link { get; set; } = "";
    public int? Percent { get; set; }
    public DateTime? LastReadUtc { get; set; }
}

public sealed class AppSettings
{
    public double PillScale { get; set; } = 1.0;
    public double PillOpacity { get; set; } = 1.0;
    public string AccentColor { get; set; } = "#FF6B6B";
    public bool ShowPercentText { get; set; } = true;

    /// <summary>Show a pill on every monitor's taskbar, not only the primary one.</summary>
    public bool ShowOnAllMonitors { get; set; } = true;
    public bool LowBatteryNotifications { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool DebugLog { get; set; }
    public int PollSeconds { get; set; } = 60;

    /// <summary>HID++ software ID (1-15) stamped on every request. 0x0E avoids the IDs used by G HUB, Solaar, LGSTray and OpenRGB.</summary>
    public int HidppSoftwareId { get; set; } = 0x0E;

    public Dictionary<string, DeviceSettings> Devices { get; set; } = [];
    public List<KnownDevice> Known { get; set; } = [];

    public DeviceSettings ForDevice(string id)
    {
        if (!Devices.TryGetValue(id, out DeviceSettings? s)) Devices[id] = s = new DeviceSettings();
        return s;
    }
}

/// <summary>JSON settings in %AppData%\PeriBattery\settings.json, written atomically.</summary>
public sealed class SettingsStore(ILog log)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeriBattery");

    public static string LogFolder => Path.Combine(DataFolder, "logs");
    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (loaded != null) return Sanitize(loaded);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.Warn("settings: could not read settings.json, using defaults: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("settings: could not save settings.json: " + ex.Message);
        }
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        s.PillScale = Math.Clamp(s.PillScale, 0.8, 1.6);
        s.PillOpacity = Math.Clamp(s.PillOpacity, 0.3, 1.0);
        s.PollSeconds = Math.Clamp(s.PollSeconds, 15, 3600);
        s.HidppSoftwareId = Math.Clamp(s.HidppSoftwareId, 1, 15);
        s.Devices ??= [];
        s.Known ??= [];
        return s;
    }
}

/// <summary>Per-user autostart through HKCU\...\Run. No admin rights involved.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PeriBattery";

    public static bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --autostart");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
