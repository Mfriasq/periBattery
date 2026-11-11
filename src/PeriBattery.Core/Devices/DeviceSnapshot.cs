namespace PeriBattery.Core.Devices;

public enum DeviceKind { Keyboard, Mouse, Headset, Gamepad, Other }

/// <summary>
/// Connected: answering right now. Asleep: its receiver is present but the device is not answering
/// (sleeping or switched off) and the last known level is kept. Disconnected: not present at all.
/// </summary>
public enum Presence { Connected, Asleep, Disconnected }

/// <summary>Immutable state of one peripheral as shown by the UI.</summary>
public sealed record DeviceSnapshot(
    string Id,
    string Name,
    DeviceKind Kind,
    string Link,
    Presence Presence,
    int? Percent,
    ChargeState Charge,
    DateTime? LastReadUtc,
    bool Unreliable = false)
{
    /// <summary>The level shown is not fresh (device asleep or disconnected).</summary>
    public bool IsStale => Presence != Presence.Connected;

    public bool IsCharging => Presence == Presence.Connected && Charge is ChargeState.Charging or ChargeState.Full;

    /// <summary>Short default label: "G703" for "G703 LIGHTSPEED Wireless Gaming Mouse w/ HERO".</summary>
    public static string ShortName(string fullName)
    {
        string trimmed = fullName.Trim();
        int space = trimmed.IndexOf(' ');
        if (space > 1 && trimmed[0] == 'G' && char.IsDigit(trimmed[1])) return trimmed[..space];
        return trimmed;
    }

    public static DeviceKind KindFromName(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("xbox") || n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense")) return DeviceKind.Gamepad;
        if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("mx anywhere")) return DeviceKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("keys")) return DeviceKind.Keyboard;
        if (n.Contains("headset") || n.Contains("headphone") || n.Contains("buds")) return DeviceKind.Headset;
        return DeviceKind.Other;
    }
}
