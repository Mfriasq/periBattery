using PeriBattery.Core.Devices;
using PeriBattery.Core.HidPp;

namespace PeriBattery.Core.Centurion;

public enum HeadsetPoll { Ok, HeadsetOffline, Failed }

/// <summary>
/// Battery reader for a Centurion headset interface. Two topologies, both observed on a G522:
///  - wired (PID 0B19): the headset answers directly; feature 0x0104 is resolved through Root.getFeature.
///  - wireless dongle (PID 0B18): the dongle has no 0x0104 (error 0x05); it exposes the bridge feature
///    0x0003, whose status says whether the headset is linked, and calls are forwarded through it.
/// </summary>
public sealed class CenturionHeadset(CenturionTransport transport)
{
    public const ushort FeatureBridge = 0x0003;
    public const ushort FeatureBatterySoc = 0x0104;

    private bool _resolved;
    private byte _bridgeIndex;
    private byte _batteryIndex;

    public CenturionTransport Transport => transport;

    /// <summary>True when calls are forwarded through the dongle's bridge (wireless mode).</summary>
    public bool IsBridged => _bridgeIndex != 0;
    public byte BridgeIndex => _bridgeIndex;
    public byte BatteryIndex => _batteryIndex;

    public async Task<(HeadsetPoll Status, BatteryReading? Reading, string Detail)> ReadBatteryAsync(CancellationToken ct = default)
    {
        if (!_resolved)
        {
            CenturionReply direct = await transport.RequestAsync(0x00, 0, [FeatureBatterySoc >> 8, FeatureBatterySoc & 0xFF], ct: ct).ConfigureAwait(false);
            if (direct.Ok && direct.Data.Length > 0 && direct.Data[0] != 0)
            {
                _batteryIndex = direct.Data[0];
                _resolved = true;
            }
            else if (direct.Status is HidPpStatus.Ok or HidPpStatus.Error)
            {
                CenturionReply bridge = await transport.RequestAsync(0x00, 0, [FeatureBridge >> 8, FeatureBridge & 0xFF], ct: ct).ConfigureAwait(false);
                if (!bridge.Ok || bridge.Data.Length == 0 || bridge.Data[0] == 0)
                    return (HeadsetPoll.Failed, null, $"no battery feature and no bridge (battery: {direct}, bridge: {bridge})");
                _bridgeIndex = bridge.Data[0];
                _resolved = true;
            }
            else
            {
                return (HeadsetPoll.Failed, null, $"root lookup: {direct}");
            }
        }

        if (!IsBridged)
        {
            CenturionReply reply = await transport.RequestAsync(_batteryIndex, 0, ct: ct).ConfigureAwait(false);
            if (!reply.Ok)
            {
                if (reply.Status == HidPpStatus.Error) Reset();
                return (HeadsetPoll.Failed, null, $"battery get: {reply}");
            }
            return (HeadsetPoll.Ok, ParseBattery(reply.Data), "direct");
        }

        // Bridge status reply: [.., .., .., connected, ..]; observed "03 E8 00 00 01" with the headset off.
        CenturionReply status = await transport.RequestAsync(_bridgeIndex, 0, ct: ct).ConfigureAwait(false);
        if (!status.Ok)
        {
            if (status.Status == HidPpStatus.Error) Reset();
            return (HeadsetPoll.Failed, null, $"bridge status: {status}");
        }
        if (status.Data.Length < 4 || status.Data[3] == 0)
            return (HeadsetPoll.HeadsetOffline, null, "bridge reports no headset");

        if (_batteryIndex == 0)
        {
            CenturionReply lookup = await transport.BridgeRequestAsync(_bridgeIndex, 0x00, 0, [FeatureBatterySoc >> 8, FeatureBatterySoc & 0xFF], ct: ct).ConfigureAwait(false);
            if (!lookup.Ok || lookup.Data.Length == 0 || lookup.Data[0] == 0)
                return (lookup.Status == HidPpStatus.Timeout ? HeadsetPoll.HeadsetOffline : HeadsetPoll.Failed, null, $"bridged battery lookup: {lookup}");
            _batteryIndex = lookup.Data[0];
        }

        CenturionReply battery = await transport.BridgeRequestAsync(_bridgeIndex, _batteryIndex, 0, ct: ct).ConfigureAwait(false);
        if (!battery.Ok)
        {
            if (battery.Status == HidPpStatus.Error) _batteryIndex = 0;
            return (battery.Status == HidPpStatus.Timeout ? HeadsetPoll.HeadsetOffline : HeadsetPoll.Failed, null, $"bridged battery get: {battery}");
        }
        return (HeadsetPoll.Ok, ParseBattery(battery.Data), "bridged");
    }

    /// <summary>True for a bridge connection event: [bridgeIndex, function 0 / software ID 0, .., connected].</summary>
    public bool IsConnectionEvent(byte[] frame) =>
        IsBridged && frame.Length >= 10 && frame[0] == CenturionTransport.ReportId && frame[4] == _bridgeIndex && frame[5] == 0x00;

    /// <summary>True for an unsolicited frame of the battery feature when talking to the headset directly.</summary>
    public bool IsBatteryEvent(byte[] frame) =>
        !IsBridged && _batteryIndex != 0 && frame.Length >= 9 && frame[0] == CenturionTransport.ReportId && frame[4] == _batteryIndex && (frame[5] & 0x0F) == 0;

    private void Reset()
    {
        _resolved = false;
        _bridgeIndex = 0;
        _batteryIndex = 0;
    }

    /// <summary>0x0104 get: [0] state of charge %, [1] second level byte, [2] charging status (0 discharging, 1/2 charging, 3 full).</summary>
    public static BatteryReading? ParseBattery(byte[] data)
    {
        if (data.Length < 3 || data[0] > 100) return null;
        ChargeState state = data[2] switch
        {
            0 => ChargeState.Discharging,
            1 or 2 => ChargeState.Charging,
            3 => ChargeState.Full,
            _ => ChargeState.Unknown,
        };
        return new BatteryReading(data[0], state, "centurion 0x0104");
    }
}
