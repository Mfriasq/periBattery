using System.Text;
using PeriBattery.Core.Devices;

namespace PeriBattery.Core.HidPp;

/// <summary>A HID++ 2.0 device behind a receiver, addressed by device index. Read-only feature calls only.</summary>
public sealed class HidPpDevice(HidPpTransport transport, byte index)
{
    private const ushort FeatureDeviceName = 0x0005;
    private static readonly ushort[] BatteryPriority = [BatteryFeatures.UnifiedBattery, BatteryFeatures.BatteryVoltage, BatteryFeatures.BatteryStatus];

    private readonly Dictionary<ushort, byte> _featureIndex = [];

    public HidPpTransport Transport => transport;
    public byte Index => index;
    public string? Name { get; private set; }
    public byte DeviceType { get; private set; } = 0xFF;
    public Version? Protocol { get; private set; }

    /// <summary>Battery feature chosen for this device (0 until resolved).</summary>
    public ushort BatteryFeatureId { get; private set; }
    public byte BatteryFeatureIndex { get; private set; }

    /// <summary>IRoot ping (feature index 0, function 1) with a random mark byte echoed back by the device.</summary>
    public async Task<HidPpReply> PingAsync(int timeoutMs = 500, int retries = 1, CancellationToken ct = default)
    {
        HidPpReply reply = default;
        // A reply with the wrong mark belongs to another client that happens to share our software ID: ask again.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte mark = (byte)Random.Shared.Next(1, 256);
            reply = await transport.RequestAsync(index, 0x00, 1, [0, 0, mark], timeoutMs, retries, retryOnTimeout: retries > 0, ct).ConfigureAwait(false);
            if (!reply.Ok) return reply;
            if (reply.Params[2] == mark)
            {
                Protocol = new Version(reply.Params[0], reply.Params[1]);
                return reply;
            }
        }
        return new HidPpReply(HidPpStatus.Timeout, []);
    }

    /// <summary>IRoot.getFeature: returns the feature index, or 0 when the device does not implement the feature.</summary>
    public async Task<(HidPpReply Reply, byte FeatureIndex)> GetFeatureIndexAsync(ushort featureId, CancellationToken ct = default)
    {
        if (_featureIndex.TryGetValue(featureId, out byte cached)) return (new HidPpReply(HidPpStatus.Ok, []), cached);
        HidPpReply reply = await transport.RequestAsync(index, 0x00, 0, [(byte)(featureId >> 8), (byte)featureId], ct: ct).ConfigureAwait(false);
        if (!reply.Ok) return (reply, 0);
        _featureIndex[featureId] = reply.Params[0];
        return (reply, reply.Params[0]);
    }

    /// <summary>Finds which battery feature the device supports (0x1004, then 0x1001, then 0x1000).</summary>
    public async Task<HidPpReply> ResolveBatteryFeatureAsync(CancellationToken ct = default)
    {
        HidPpReply last = new(HidPpStatus.Ok, []);
        foreach (ushort id in BatteryPriority)
        {
            (HidPpReply reply, byte featureIndex) = await GetFeatureIndexAsync(id, ct).ConfigureAwait(false);
            last = reply;
            if (!reply.Ok) return reply;
            if (featureIndex == 0) continue;
            BatteryFeatureId = id;
            BatteryFeatureIndex = featureIndex;
            return reply;
        }
        return last;
    }

    public async Task<(HidPpReply Reply, BatteryReading? Reading)> ReadBatteryAsync(CancellationToken ct = default)
    {
        if (BatteryFeatureIndex == 0)
        {
            HidPpReply resolve = await ResolveBatteryFeatureAsync(ct).ConfigureAwait(false);
            if (!resolve.Ok || BatteryFeatureIndex == 0) return (resolve, null);
        }
        HidPpReply reply = await transport.RequestAsync(index, BatteryFeatureIndex, BatteryFeatures.GetterFunction(BatteryFeatureId), ct: ct).ConfigureAwait(false);
        return (reply, reply.Ok ? BatteryFeatures.Parse(BatteryFeatureId, reply.Params) : null);
    }

    /// <summary>Reads the device name and type through feature 0x0005 (read-only).</summary>
    public async Task ReadNameAsync(CancellationToken ct = default)
    {
        (HidPpReply reply, byte fi) = await GetFeatureIndexAsync(FeatureDeviceName, ct).ConfigureAwait(false);
        if (!reply.Ok || fi == 0) return;

        HidPpReply count = await transport.RequestAsync(index, fi, 0, ct: ct).ConfigureAwait(false);
        if (!count.Ok) return;
        int length = count.Params[0];
        var sb = new StringBuilder();
        while (sb.Length < length)
        {
            HidPpReply chunk = await transport.RequestAsync(index, fi, 1, [(byte)sb.Length], ct: ct).ConfigureAwait(false);
            if (!chunk.Ok) break;
            int take = Math.Min(16, length - sb.Length);
            string text = Encoding.ASCII.GetString(chunk.Params, 0, take).TrimEnd('\0');
            if (text.Length == 0) break;
            sb.Append(text);
        }
        if (sb.Length > 0) Name = sb.ToString();

        HidPpReply type = await transport.RequestAsync(index, fi, 2, ct: ct).ConfigureAwait(false);
        if (type.Ok) DeviceType = type.Params[0];
    }

    /// <summary>True when the frame is a broadcast event (software ID 0, function 0) of this device's battery feature.</summary>
    public bool TryParseBatteryEvent(byte[] frame, out BatteryReading? reading)
    {
        reading = null;
        if (BatteryFeatureIndex == 0 || frame.Length < 20 || frame[1] != index || frame[2] != BatteryFeatureIndex || frame[3] != 0x00) return false;
        byte[] p = new byte[16];
        Buffer.BlockCopy(frame, 4, p, 0, 16);
        reading = BatteryFeatures.Parse(BatteryFeatureId, p);
        return reading != null;
    }
}
