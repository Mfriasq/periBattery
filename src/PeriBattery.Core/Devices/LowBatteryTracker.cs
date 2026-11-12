namespace PeriBattery.Core.Devices;

/// <summary>
/// Decides when to raise the low-battery notification: once per discharge cycle when a connected
/// device drops to the threshold, re-armed when it starts charging or rises above the re-arm level.
/// </summary>
public sealed class LowBatteryTracker(int threshold = 10, int rearmAbove = 15)
{
    private readonly HashSet<string> _notified = [];

    /// <summary>Returns true when a notification should be shown for this snapshot.</summary>
    public bool ShouldNotify(DeviceSnapshot device)
    {
        if (device.Presence != Presence.Connected || device.Percent is not int percent) return false;

        if (device.IsCharging || percent > rearmAbove)
        {
            _notified.Remove(device.Id);
            return false;
        }

        if (percent <= threshold && _notified.Add(device.Id)) return true;
        return false;
    }
}
