using PeriBattery.Core.Devices;
using Xunit;

namespace PeriBattery.Tests;

public class LowBatteryTrackerTests
{
    private static DeviceSnapshot Device(int? percent, ChargeState charge = ChargeState.Discharging, Presence presence = Presence.Connected) =>
        new("id", "G703", DeviceKind.Mouse, "LIGHTSPEED", presence, percent, charge, DateTime.UtcNow);

    [Fact]
    public void NotifiesOncePerDischargeCycle()
    {
        var tracker = new LowBatteryTracker();
        Assert.False(tracker.ShouldNotify(Device(11)));
        Assert.True(tracker.ShouldNotify(Device(10)));
        Assert.False(tracker.ShouldNotify(Device(9)));
        Assert.False(tracker.ShouldNotify(Device(12))); // still below the re-arm level
        Assert.False(tracker.ShouldNotify(Device(8)));
    }

    [Fact]
    public void RearmsAboveFifteenPercent()
    {
        var tracker = new LowBatteryTracker();
        Assert.True(tracker.ShouldNotify(Device(9)));
        Assert.False(tracker.ShouldNotify(Device(16)));
        Assert.True(tracker.ShouldNotify(Device(10)));
    }

    [Fact]
    public void RearmsWhenCharging()
    {
        var tracker = new LowBatteryTracker();
        Assert.True(tracker.ShouldNotify(Device(7)));
        Assert.False(tracker.ShouldNotify(Device(8, ChargeState.Charging)));
        Assert.True(tracker.ShouldNotify(Device(8)));
    }

    [Fact]
    public void IgnoresStaleAndUnknownLevels()
    {
        var tracker = new LowBatteryTracker();
        Assert.False(tracker.ShouldNotify(Device(5, presence: Presence.Asleep)));
        Assert.False(tracker.ShouldNotify(Device(5, presence: Presence.Disconnected)));
        Assert.False(tracker.ShouldNotify(Device(null)));
        Assert.True(tracker.ShouldNotify(Device(5)));
    }
}
