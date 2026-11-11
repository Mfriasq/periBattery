using PeriBattery.Core.Devices;

namespace PeriBattery.Core.HidPp;

/// <summary>
/// Parsers for the three HID++ 2.0 battery features. Layouts verified against the Linux kernel
/// (hid-logitech-hidpp.c), Solaar (hidpp20.py) and LGSTrayBattery (Features/Battery100x.cs).
/// The same layouts apply to the unsolicited broadcast events (function 0, software ID 0).
/// </summary>
public static class BatteryFeatures
{
    public const ushort BatteryStatus = 0x1000;
    public const ushort BatteryVoltage = 0x1001;
    public const ushort UnifiedBattery = 0x1004;

    /// <summary>Getter function number for each feature (the one that returns the current level).</summary>
    public static byte GetterFunction(ushort featureId) => featureId == UnifiedBattery ? (byte)1 : (byte)0;

    public static BatteryReading? Parse(ushort featureId, byte[] p) => featureId switch
    {
        BatteryStatus => ParseBatteryStatus(p),
        BatteryVoltage => ParseBatteryVoltage(p),
        UnifiedBattery => ParseUnifiedBattery(p),
        _ => null,
    };

    /// <summary>0x1000 getBatteryLevelStatus: [0] level %, [1] next level %, [2] status.</summary>
    public static BatteryReading ParseBatteryStatus(byte[] p)
    {
        ChargeState state = p[2] switch
        {
            0 => ChargeState.Discharging,
            1 or 2 or 4 => ChargeState.Charging,
            3 => ChargeState.Full,
            _ => ChargeState.Error,
        };
        int? percent = p[0];
        if (state == ChargeState.Full) percent = 100;
        // While charging the device reports 0 = unknown.
        else if (state != ChargeState.Discharging && p[0] == 0) percent = null;
        if (percent > 100) percent = null;
        return new BatteryReading(percent, state, "0x1000");
    }

    /// <summary>0x1001 getBatteryVoltage: [0..1] millivolts big-endian, [2] flags (bit 7 = external power, bits 0-2 = charge status).</summary>
    public static BatteryReading ParseBatteryVoltage(byte[] p)
    {
        int mv = (p[0] << 8) | p[1];
        byte flags = p[2];
        ChargeState state = ChargeState.Discharging;
        if ((flags & 0x80) != 0)
        {
            state = (flags & 0x07) switch
            {
                0 => ChargeState.Charging,
                1 => ChargeState.Full,
                2 => ChargeState.Discharging, // powered but not charging
                _ => ChargeState.Unknown,
            };
        }
        int? percent = mv == 0 ? null : VoltageCurve.ToPercent(mv);
        if (state == ChargeState.Full) percent = 100;
        return new BatteryReading(percent, state, "0x1001", mv);
    }

    /// <summary>0x1004 get_status: [0] state of charge %, [1] level bitmask, [2] charging status, [3] external power.</summary>
    public static BatteryReading ParseUnifiedBattery(byte[] p)
    {
        ChargeState state = p[2] switch
        {
            0 => ChargeState.Discharging,
            1 or 2 => ChargeState.Charging,
            3 => ChargeState.Full,
            _ => ChargeState.Error,
        };
        int? percent = p[0];
        if (p[0] == 0)
        {
            // No state-of-charge support: approximate from the level bitmask (full/good/low/critical).
            percent = (p[1] & 0x08) != 0 ? 100 : (p[1] & 0x04) != 0 ? 50 : (p[1] & 0x02) != 0 ? 20 : (p[1] & 0x01) != 0 ? 5 : null;
        }
        if (percent > 100) percent = null;
        return new BatteryReading(percent, state, "0x1004");
    }
}

/// <summary>Li-Po discharge curve shared by the Linux kernel and LGSTrayBattery: entry i is the minimum voltage for (100 - i) %.</summary>
public static class VoltageCurve
{
    private static readonly int[] Table =
    [
        4186, 4156, 4143, 4133, 4122, 4113, 4103, 4094, 4086, 4075,
        4067, 4059, 4051, 4043, 4035, 4027, 4019, 4011, 4003, 3997,
        3989, 3983, 3976, 3969, 3961, 3955, 3949, 3942, 3935, 3929,
        3922, 3916, 3909, 3902, 3896, 3890, 3883, 3877, 3870, 3865,
        3859, 3853, 3848, 3842, 3837, 3833, 3828, 3824, 3819, 3815,
        3811, 3808, 3804, 3800, 3797, 3793, 3790, 3787, 3784, 3781,
        3778, 3775, 3772, 3770, 3767, 3764, 3762, 3759, 3757, 3754,
        3751, 3748, 3744, 3741, 3737, 3734, 3730, 3726, 3724, 3720,
        3717, 3714, 3710, 3706, 3702, 3697, 3693, 3688, 3683, 3677,
        3671, 3666, 3662, 3658, 3654, 3646, 3633, 3612, 3579, 3537,
    ];

    public static int ToPercent(int millivolts)
    {
        for (int i = 0; i < Table.Length; i++)
            if (millivolts >= Table[i]) return 100 - i;
        return 0;
    }
}
