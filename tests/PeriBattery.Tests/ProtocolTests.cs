using PeriBattery.Core.Centurion;
using PeriBattery.Core.Devices;
using PeriBattery.Core.HidPp;
using Xunit;

namespace PeriBattery.Tests;

public class ProtocolTests
{
    private static byte[] Bytes(string hex, int length = 0)
    {
        byte[] data = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();
        if (length > data.Length) Array.Resize(ref data, length);
        return data;
    }

    // Frames below were captured from the real devices with G HUB running.

    [Fact]
    public void BatteryVoltage_G703_Capture()
    {
        BatteryReading r = BatteryFeatures.ParseBatteryVoltage(Bytes("0E A0 00 00", 16));
        Assert.Equal(3744, r.Millivolts);
        Assert.Equal(28, r.Percent);
        Assert.Equal(ChargeState.Discharging, r.State);
    }

    [Theory]
    [InlineData("10 68 80", ChargeState.Charging)]
    [InlineData("10 68 81", ChargeState.Full)]
    [InlineData("10 68 82", ChargeState.Discharging)]
    public void BatteryVoltage_Flags(string hex, ChargeState expected)
    {
        Assert.Equal(expected, BatteryFeatures.ParseBatteryVoltage(Bytes(hex, 16)).State);
    }

    [Fact]
    public void UnifiedBattery_G515_Capture()
    {
        BatteryReading r = BatteryFeatures.ParseUnifiedBattery(Bytes("63 08 00 00", 16));
        Assert.Equal(99, r.Percent);
        Assert.Equal(ChargeState.Discharging, r.State);
    }

    [Fact]
    public void UnifiedBattery_Charging_And_LevelFallback()
    {
        Assert.Equal(ChargeState.Charging, BatteryFeatures.ParseUnifiedBattery(Bytes("32 04 01 01", 16)).State);
        Assert.Equal(ChargeState.Full, BatteryFeatures.ParseUnifiedBattery(Bytes("64 08 03 01", 16)).State);
        Assert.Equal(20, BatteryFeatures.ParseUnifiedBattery(Bytes("00 02 00 00", 16)).Percent);
    }

    [Fact]
    public void BatteryStatus_ChargingReportsUnknownLevel()
    {
        Assert.Equal(55, BatteryFeatures.ParseBatteryStatus(Bytes("37 32 00", 16)).Percent);
        Assert.Null(BatteryFeatures.ParseBatteryStatus(Bytes("00 00 01", 16)).Percent);
        Assert.Equal(100, BatteryFeatures.ParseBatteryStatus(Bytes("00 00 03", 16)).Percent);
    }

    [Theory]
    [InlineData(4200, 100)]
    [InlineData(4186, 100)]
    [InlineData(4185, 99)]
    [InlineData(3811, 50)]
    [InlineData(3537, 1)]
    [InlineData(3500, 0)]
    public void VoltageCurve_Points(int millivolts, int expected)
    {
        Assert.Equal(expected, VoltageCurve.ToPercent(millivolts));
    }

    [Fact]
    public void ForeignReplies_AreRecognisedBySoftwareId()
    {
        const byte own = 0x0E;
        Assert.False(HidPpTransport.IsForeignReply(Bytes("11 01 08 1E 63 08 00", 20), own)); // ours
        Assert.True(HidPpTransport.IsForeignReply(Bytes("11 01 08 1D 63 08 00", 20), own));  // another client (0x0D)
        Assert.False(HidPpTransport.IsForeignReply(Bytes("11 01 08 00 63 08 00", 20), own)); // broadcast event
        Assert.False(HidPpTransport.IsForeignReply(Bytes("10 01 41 11 71 B4 40"), own));     // connection notification
        Assert.True(HidPpTransport.IsForeignReply(Bytes("10 02 8F 00 1D 08 00"), own));      // someone else's error
        Assert.False(HidPpTransport.IsForeignReply(Bytes("10 02 8F 00 1E 08 00"), own));     // our error
    }

    [Fact]
    public void Centurion_BuildFrame_MatchesReferenceBytes()
    {
        // Root.getFeature(0x0003) as sent to the G522 dongle.
        byte[] frame = CenturionTransport.BuildFrame(0x23, 0x00, 0x0E, [0x00, 0x03]);
        Assert.Equal(64, frame.Length);
        Assert.Equal(Bytes("50 23 05 00 00 0E 00 03"), frame[..8]);
    }

    [Fact]
    public void Centurion_DirectReply_And_Error_Captures()
    {
        CenturionReply? ok = CenturionTransport.MatchDirect(Bytes("50 23 06 00 05 0E 3A 3C 02", 64), 0x23, 0x05, 0x0E);
        Assert.True(ok.HasValue && ok.Value.Ok);
        BatteryReading? reading = CenturionHeadset.ParseBattery(ok!.Value.Data);
        Assert.Equal(58, reading!.Percent);
        Assert.Equal(ChargeState.Charging, reading.State);

        // The dongle has no 0x0104 feature: error 0x05.
        CenturionReply? error = CenturionTransport.MatchDirect(Bytes("50 23 05 00 FF 00 0E 05", 64), 0x23, 0x00, 0x0E);
        Assert.True(error.HasValue);
        Assert.Equal(HidPpStatus.Error, error!.Value.Status);
        Assert.Equal(0x05, error.Value.ErrorCode);

        // A frame for another software ID is not ours.
        Assert.Null(CenturionTransport.MatchDirect(Bytes("50 23 06 00 05 0D 3A 3C 02", 64), 0x23, 0x05, 0x0E));
    }

    [Fact]
    public void Centurion_BridgeAck_IsNotTheAnswer()
    {
        // Acknowledgement of a bridged call (our software ID): must not complete the request.
        Assert.Null(CenturionTransport.MatchBridge(Bytes("50 23 03 00 03 1E 00 00", 64), 0x23, 0x03, 0x1E, 0x00, 0x0E));
        // Bridge event carrying the headset's answer: [.., bridge, 0x10, 00, len, 00, subFeature, subFn|sw, payload].
        CenturionReply? reply = CenturionTransport.MatchBridge(Bytes("50 23 0B 00 03 10 00 06 00 05 0E 28 28 00", 64), 0x23, 0x03, 0x1E, 0x05, 0x0E);
        Assert.True(reply.HasValue && reply.Value.Ok);
        Assert.Equal(40, CenturionHeadset.ParseBattery(reply!.Value.Data)!.Percent);
    }

    [Theory]
    [InlineData("G703 LIGHTSPEED Wireless Gaming Mouse w/ HERO", "G703")]
    [InlineData("G515 LS TKL", "G515")]
    [InlineData("Xbox Wireless Controller", "Xbox Wireless Controller")]
    public void ShortName(string full, string expected)
    {
        Assert.Equal(expected, DeviceSnapshot.ShortName(full));
    }
}
