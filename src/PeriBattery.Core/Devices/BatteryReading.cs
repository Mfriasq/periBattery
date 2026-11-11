namespace PeriBattery.Core.Devices;

public enum ChargeState { Unknown, Discharging, Charging, Full, Error }

/// <summary>One battery measurement. <see cref="Percent"/> is null when the device does not report a level (e.g. some devices while charging).</summary>
public sealed record BatteryReading(int? Percent, ChargeState State, string Source, int? Millivolts = null)
{
    public bool IsCharging => State is ChargeState.Charging or ChargeState.Full;

    public override string ToString() =>
        $"{(Percent.HasValue ? Percent + "%" : "?%")} {State.ToString().ToLowerInvariant()}" +
        (Millivolts.HasValue ? $" {Millivolts} mV" : "") + $" [{Source}]";
}
