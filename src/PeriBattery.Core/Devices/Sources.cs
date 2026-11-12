using PeriBattery.Core.Bluetooth;
using PeriBattery.Core.Centurion;
using PeriBattery.Core.Hid;
using PeriBattery.Core.HidPp;
using PeriBattery.Core.Infra;

namespace PeriBattery.Core.Devices;

/// <summary>Callbacks a source uses to talk back to the <see cref="DeviceManager"/>.</summary>
internal interface ISourceHost
{
    ILog Log { get; }
    byte SoftwareId { get; }
    void Publish();
    void RequestRefresh(int delayMs);
}

internal interface IDeviceSource : IDisposable
{
    /// <summary>Identifies the HID collection group this source owns (empty for non-HID sources).</summary>
    string Key { get; }
    bool IsDead { get; }
    Task PollAsync(bool rediscover, CancellationToken ct);
    IReadOnlyList<DeviceSnapshot> Snapshots { get; }
    string Diagnostics { get; }
}

/// <summary>One LIGHTSPEED/Unifying-style receiver and the HID++ 2.0 devices paired to it.</summary>
internal sealed class HidPpReceiverSource : IDeviceSource
{
    // 0xFF first: a device plugged in directly with its USB cable (e.g. a G703 in wired mode, PID C090)
    // answers there, and in fact answers on every index, so nothing else is scanned once it is found.
    // A real receiver rejects 0xFF and its paired devices live in slots 1..6.
    private static readonly byte[] DeviceIndexes = [0xFF, 1, 2, 3, 4, 5, 6];
    private const byte WiredIndex = 0xFF;
    private const int UnreliableAfterFailures = 3;

    private sealed class Entry(HidPpDevice device)
    {
        public readonly HidPpDevice Device = device;
        public DeviceSnapshot? Snapshot;
        public DateTime IgnoreEventsUntilUtc;
        public int ConsecutiveTimeouts;
    }

    private readonly ISourceHost _host;
    private readonly HidPpTransport _transport;
    private readonly string _tag;
    private readonly object _lock = new();
    private readonly Dictionary<byte, Entry> _entries = [];
    private int _quickRetries;
    private bool _direct;

    private HidPpReceiverSource(string key, HidPpTransport transport, ISourceHost host, string tag)
    {
        Key = key;
        _transport = transport;
        _host = host;
        _tag = tag;
        _transport.Notification += OnNotification;
    }

    public string Key { get; }
    public bool IsDead => _transport.IsClosed;
    public string Diagnostics => $"{_tag}: {_transport.Stats}";

    public static HidPpReceiverSource? TryCreate(string key, IEnumerable<HidDeviceInfo> collections, ISourceHost host)
    {
        // Only the two HID++ collections are ever opened: usage 0x0002 (long, read/write) and usage 0x0001 (short, read-only).
        HidDeviceInfo? longInfo = collections.FirstOrDefault(c => c.UsagePage == 0xFF00 && c.Usage == 0x0002);
        HidDeviceInfo? shortInfo = collections.FirstOrDefault(c => c.UsagePage == 0xFF00 && c.Usage == 0x0001);
        if (longInfo == null) return null;

        HidChannel? longCh = HidChannel.TryOpen(longInfo, writable: true, host.Log);
        if (longCh == null) return null;
        HidChannel? shortCh = shortInfo != null ? HidChannel.TryOpen(shortInfo, writable: false, host.Log) : null;

        string tag = $"hidpp {longInfo.ProductId:X4}";
        host.Log.Info($"{tag}: receiver opened in shared mode (short collection {(shortCh != null ? "open, read-only" : "unavailable")})");
        return new HidPpReceiverSource(key, new HidPpTransport(longCh, shortCh, host.SoftwareId, host.Log, tag), host, tag);
    }

    public IReadOnlyList<DeviceSnapshot> Snapshots
    {
        get { lock (_lock) return _entries.Values.Where(e => e.Snapshot != null).Select(e => e.Snapshot!).ToList(); }
    }

    public async Task PollAsync(bool rediscover, CancellationToken ct)
    {
        bool anyKnown;
        lock (_lock) anyKnown = _entries.Count > 0;
        bool discoveryIncomplete = false;

        foreach (byte index in DeviceIndexes)
        {
            if (ct.IsCancellationRequested || _transport.IsClosed) break;
            if (_direct && index != WiredIndex) continue;
            Entry? entry;
            lock (_lock) _entries.TryGetValue(index, out entry);

            if (entry == null)
            {
                if (!rediscover && anyKnown) continue;
                var device = new HidPpDevice(_transport, index);
                // Index 1 is where a LIGHTSPEED receiver keeps its device. An idle keyboard can take over a
                // second to answer the first request, so that index gets a longer timeout and retries.
                HidPpReply ping = index == 1
                    ? await device.PingAsync(timeoutMs: 700, retries: 2, ct).ConfigureAwait(false)
                    : await device.PingAsync(timeoutMs: 400, retries: 0, ct).ConfigureAwait(false);
                if (!ping.Ok)
                {
                    // Only the slot that normally holds the device justifies a quick retry; a silent
                    // empty slot on a receiver whose device is already known is not worth re-polling for.
                    if (ping.Status == HidPpStatus.Timeout && index == 1) discoveryIncomplete = true;
                    continue;
                }

                await device.ReadNameAsync(ct).ConfigureAwait(false);
                HidPpReply resolve = await device.ResolveBatteryFeatureAsync(ct).ConfigureAwait(false);
                if (device.Name == null || !resolve.Ok)
                {
                    discoveryIncomplete = true; // retried shortly
                    continue;
                }
                if (device.BatteryFeatureIndex == 0)
                {
                    _host.Log.Info($"{_tag}: \"{device.Name}\" (index {index}) exposes no battery feature (0x1000/0x1001/0x1004)");
                }
                else
                {
                    _host.Log.Info($"{_tag}: found \"{device.Name}\" at index {index}, HID++ {device.Protocol}, battery feature 0x{device.BatteryFeatureId:X4} @ {device.BatteryFeatureIndex}");
                }
                entry = new Entry(device);
                lock (_lock) _entries[index] = entry;
                if (index == WiredIndex && device.BatteryFeatureIndex != 0) _direct = true;
            }

            if (entry.Device.BatteryFeatureIndex == 0) continue;
            await PollDeviceAsync(entry, ct).ConfigureAwait(false);
        }

        // A request that got no answer at all during discovery (lost to other traffic on the shared
        // receiver) should not leave the device missing for a whole poll interval: retry soon, a few times.
        if (discoveryIncomplete && _quickRetries < 3)
        {
            _quickRetries++;
            _host.Log.Info($"{_tag}: discovery incomplete (no answer), retrying in 5 s ({_quickRetries}/3)");
            _host.RequestRefresh(5000);
        }
        else if (!discoveryIncomplete)
        {
            _quickRetries = 0;
        }
    }

    private async Task PollDeviceAsync(Entry entry, CancellationToken ct)
    {
        HidPpDevice device = entry.Device;
        (HidPpReply reply, BatteryReading? reading) = await device.ReadBatteryAsync(ct).ConfigureAwait(false);

        lock (_lock)
        {
            DeviceSnapshot previous = entry.Snapshot ?? NewSnapshot(device);
            if (device.Index == WiredIndex) previous = previous with { Link = "USB" };
            if (reading != null)
            {
                entry.ConsecutiveTimeouts = 0;
                entry.Snapshot = previous with
                {
                    Presence = Presence.Connected,
                    Percent = reading.Percent ?? previous.Percent,
                    Charge = reading.State,
                    LastReadUtc = DateTime.UtcNow,
                    Unreliable = false,
                };
                _host.Log.Debug($"{_tag}: {previous.Name} = {reading}");
                return;
            }

            if (reply.Status == HidPpStatus.Timeout)
            {
                // No answer at all: either asleep without the receiver saying so, or lost to interference.
                entry.ConsecutiveTimeouts++;
                bool unreliable = entry.ConsecutiveTimeouts >= UnreliableAfterFailures;
                if (entry.ConsecutiveTimeouts == UnreliableAfterFailures)
                    _host.Log.Warn($"{_tag}: {previous.Name} did not answer {UnreliableAfterFailures} polls in a row ({_transport.Stats}); reading marked unreliable");
                entry.Snapshot = previous with { Presence = Presence.Asleep, Unreliable = unreliable };
            }
            else
            {
                if (previous.Presence == Presence.Connected)
                    _host.Log.Info($"{_tag}: {previous.Name} is not reachable ({reply}); keeping last level {previous.Percent}%");
                entry.ConsecutiveTimeouts = 0;
                entry.Snapshot = previous with { Presence = Presence.Asleep, Unreliable = false };
            }
        }
    }

    private static DeviceSnapshot NewSnapshot(HidPpDevice device)
    {
        string name = device.Name ?? "Logitech";
        DeviceKind kind = device.DeviceType switch
        {
            0 or 2 => DeviceKind.Keyboard,
            3 or 4 or 5 => DeviceKind.Mouse,
            8 => DeviceKind.Headset,
            _ => DeviceSnapshot.KindFromName(name),
        };
        return new DeviceSnapshot("hidpp:" + name, DeviceSnapshot.ShortName(name), kind, "LIGHTSPEED", Presence.Asleep, null, ChargeState.Unknown, null);
    }

    private void OnNotification(byte[] frame)
    {
        // HID++ 1.0 device connection notification: [0x10, index, 0x41, protocol, flags, wpidLo, wpidHi]; flags bit 6 = link lost.
        if (frame[0] == HidPpTransport.ShortReportId && frame[2] == 0x41)
        {
            bool offline = (frame[4] & 0x40) != 0;
            _host.Log.Debug($"{_tag}: connection notification index {frame[1]} {(offline ? "offline" : "online")}");
            bool known;
            lock (_lock)
            {
                known = _entries.TryGetValue(frame[1], out Entry? entry);
                if (entry != null)
                {
                    // Devices may broadcast a bogus level right after waking up: ignore events briefly, then poll.
                    entry.IgnoreEventsUntilUtc = DateTime.UtcNow.AddSeconds(5);
                    if (offline && entry.Snapshot != null) entry.Snapshot = entry.Snapshot with { Presence = Presence.Asleep };
                }
            }
            if (offline && known) _host.Publish();
            if (!offline) _host.RequestRefresh(known ? 4000 : 1500);
            return;
        }

        if (frame[0] != HidPpTransport.LongReportId) return;
        bool changed = false;
        lock (_lock)
        {
            if (_entries.TryGetValue(frame[1], out Entry? entry) && entry.Snapshot != null &&
                DateTime.UtcNow >= entry.IgnoreEventsUntilUtc &&
                entry.Device.TryParseBatteryEvent(frame, out BatteryReading? reading) && reading != null)
            {
                entry.Snapshot = entry.Snapshot with
                {
                    Presence = Presence.Connected,
                    Percent = reading.Percent ?? entry.Snapshot.Percent,
                    Charge = reading.State,
                    LastReadUtc = DateTime.UtcNow,
                };
                _host.Log.Debug($"{_tag}: battery event {entry.Snapshot.Name} = {reading}");
                changed = true;
            }
        }
        if (changed) _host.Publish();
    }

    public void Dispose()
    {
        _transport.Notification -= OnNotification;
        _transport.Dispose();
    }
}

/// <summary>One Centurion headset interface: the wireless dongle or the headset itself in USB mode.</summary>
internal sealed class CenturionSource : IDeviceSource
{
    private const int UnreliableAfterFailures = 3;

    private readonly ISourceHost _host;
    private readonly CenturionTransport _transport;
    private readonly CenturionHeadset _headset;
    private readonly string _tag;
    private readonly string _fullName;
    private readonly object _lock = new();
    private DeviceSnapshot _snapshot;
    private int _failures;
    private DateTime _lastEventRefreshUtc;

    private CenturionSource(string key, CenturionTransport transport, ISourceHost host, string tag, string fullName)
    {
        Key = key;
        _transport = transport;
        _headset = new CenturionHeadset(transport);
        _host = host;
        _tag = tag;
        _fullName = fullName;
        _snapshot = new DeviceSnapshot("centurion:" + fullName, DeviceSnapshot.ShortName(fullName), DeviceKind.Headset, "LIGHTSPEED",
            Presence.Asleep, null, ChargeState.Unknown, null);
        _transport.Notification += OnNotification;
    }

    public string Key { get; }
    public bool IsDead => _transport.IsClosed;
    public string Diagnostics => $"{_tag}: {_transport.Stats}";

    public static bool IsCenturion(HidDeviceInfo c) =>
        c.UsagePage == 0xFFA0 && c.InputReportIds.Contains(CenturionTransport.ReportId) && c.OutputReportIds.Contains(CenturionTransport.ReportId);

    public static CenturionSource? TryCreate(string key, IEnumerable<HidDeviceInfo> collections, ISourceHost host)
    {
        HidDeviceInfo? info = collections.FirstOrDefault(IsCenturion);
        if (info == null) return null;
        HidChannel? channel = HidChannel.TryOpen(info, writable: true, host.Log);
        if (channel == null) return null;

        // "G522 LIGHTSPEED - Wireless Mode" and "G522 LIGHTSPEED - USB Mode" are the same headset.
        string name = info.Product;
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) name = name[..dash];
        if (name.Length == 0) name = $"Logitech {info.ProductId:X4}";

        string tag = $"centurion {info.ProductId:X4}";
        host.Log.Info($"{tag}: \"{info.Product}\" opened in shared mode");
        return new CenturionSource(key, new CenturionTransport(channel, host.SoftwareId, host.Log, tag), host, tag, name);
    }

    public IReadOnlyList<DeviceSnapshot> Snapshots
    {
        get { lock (_lock) return [_snapshot]; }
    }

    public async Task PollAsync(bool rediscover, CancellationToken ct)
    {
        (HeadsetPoll status, BatteryReading? reading, string detail) = await _headset.ReadBatteryAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            string link = _headset.IsBridged ? "LIGHTSPEED" : "USB";
            switch (status)
            {
                case HeadsetPoll.Ok when reading != null:
                    _failures = 0;
                    _snapshot = _snapshot with
                    {
                        Link = link,
                        Presence = Presence.Connected,
                        Percent = reading.Percent ?? _snapshot.Percent,
                        Charge = reading.State,
                        LastReadUtc = DateTime.UtcNow,
                        Unreliable = false,
                    };
                    _host.Log.Debug($"{_tag}: {_fullName} = {reading} ({detail})");
                    break;

                case HeadsetPoll.HeadsetOffline:
                    if (_snapshot.Presence == Presence.Connected) _host.Log.Info($"{_tag}: headset offline ({detail})");
                    _failures = 0;
                    _snapshot = _snapshot with { Link = link, Presence = Presence.Asleep, Unreliable = false };
                    break;

                default:
                    _failures++;
                    if (_failures == UnreliableAfterFailures)
                        _host.Log.Warn($"{_tag}: {UnreliableAfterFailures} failed polls in a row ({detail}; {_transport.Stats}); reading marked unreliable");
                    else
                        _host.Log.Debug($"{_tag}: poll failed ({detail})");
                    _snapshot = _snapshot with { Presence = Presence.Asleep, Unreliable = _failures >= UnreliableAfterFailures };
                    break;
            }
        }
    }

    private void OnNotification(byte[] frame)
    {
        if (!_headset.IsConnectionEvent(frame) && !_headset.IsBatteryEvent(frame)) return;
        // Rate-limit: other clients' traffic must not turn into a polling storm.
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            if ((now - _lastEventRefreshUtc).TotalSeconds < 5) return;
            _lastEventRefreshUtc = now;
        }
        _host.Log.Debug($"{_tag}: event {frame.Hex()}");
        _host.RequestRefresh(1500);
    }

    public void Dispose()
    {
        _transport.Notification -= OnNotification;
        _transport.Dispose();
    }
}

/// <summary>Bluetooth LE devices whose battery level Windows tracks (Xbox Wireless Controller).</summary>
internal sealed class BluetoothSource(ISourceHost host) : IDeviceSource
{
    private readonly object _lock = new();
    private List<DeviceSnapshot> _snapshots = [];

    public string Key => "";
    public bool IsDead => false;
    public string Diagnostics => "bluetooth: n/a";

    public IReadOnlyList<DeviceSnapshot> Snapshots
    {
        get { lock (_lock) return _snapshots; }
    }

    public Task PollAsync(bool rediscover, CancellationToken ct)
    {
        var list = new List<DeviceSnapshot>();
        try
        {
            foreach (BluetoothBatteryInfo info in BluetoothBatteryReader.Read())
            {
                list.Add(new DeviceSnapshot("bt:" + info.Address, info.Name, DeviceSnapshot.KindFromName(info.Name), "Bluetooth",
                    info.Connected ? Presence.Connected : Presence.Disconnected, info.Percent, ChargeState.Unknown,
                    info.Connected ? DateTime.UtcNow : null));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            host.Log.Warn("bluetooth: cannot read battery properties: " + ex.Message);
        }
        lock (_lock) _snapshots = list;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
