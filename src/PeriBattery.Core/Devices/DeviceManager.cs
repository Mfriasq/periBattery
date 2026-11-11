using PeriBattery.Core.Hid;
using PeriBattery.Core.Infra;

namespace PeriBattery.Core.Devices;

/// <summary>
/// Owns every battery source and runs the single background polling loop (default: every 60 s, plus
/// on-demand refreshes after plug/unplug or device notifications). All device I/O happens on the thread
/// pool; <see cref="Changed"/> is raised from a background thread.
/// </summary>
public sealed class DeviceManager : ISourceHost, IDisposable
{
    private const ushort LogitechVid = 0x046D;
    private const int RediscoverEveryCycles = 10;

    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _lock = new();
    private readonly List<IDeviceSource> _sources = [];
    private readonly Dictionary<string, DeviceSnapshot> _known = [];
    private readonly Dictionary<string, int> _openFailures = [];
    private IReadOnlyList<DeviceSnapshot> _current = [];
    private int _pendingDelayMs = -1;
    private bool _pendingRediscover;
    private Task? _loop;

    public DeviceManager(ILog log, byte softwareId, IEnumerable<DeviceSnapshot>? lastKnown = null)
    {
        _log = log;
        SoftwareId = softwareId;
        if (lastKnown != null)
        {
            foreach (DeviceSnapshot s in lastKnown)
                _known[s.Id] = s with { Presence = Presence.Disconnected, Charge = ChargeState.Unknown, Unreliable = false };
            _current = Order(_known.Values);
        }
    }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
    public byte SoftwareId { get; }
    ILog ISourceHost.Log => _log;

    /// <summary>Latest merged device list, including disconnected devices with their last known level.</summary>
    public IReadOnlyList<DeviceSnapshot> Current
    {
        get { lock (_lock) return _current; }
    }

    public event Action<IReadOnlyList<DeviceSnapshot>>? Changed;

    /// <summary>Per-source request/timeout/busy/foreign-frame counters, for judging coexistence with G HUB.</summary>
    public IReadOnlyList<string> Diagnostics
    {
        get { lock (_lock) return _sources.Select(s => s.Diagnostics).ToList(); }
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>Asks for an early poll (e.g. after WM_DEVICECHANGE). Calls are coalesced.</summary>
    public void RequestRefresh(int delayMs = 0, bool rediscover = true)
    {
        lock (_lock)
        {
            _pendingDelayMs = _pendingDelayMs < 0 ? delayMs : Math.Min(_pendingDelayMs, delayMs);
            _pendingRediscover |= rediscover;
        }
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    void ISourceHost.RequestRefresh(int delayMs) => RequestRefresh(delayMs, rediscover: true);

    void ISourceHost.Publish() => Publish();

    /// <summary>Forgets a disconnected device so it no longer shows up with its last known level.</summary>
    public void Forget(string id)
    {
        lock (_lock) _known.Remove(id);
        Publish();
    }

    private async Task LoopAsync()
    {
        CancellationToken ct = _cts.Token;
        int cycle = 0;
        bool rediscover = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CycleAsync(rediscover || cycle % RediscoverEveryCycles == 0, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Never let a device or driver hiccup take the loop down.
                _log.Warn($"poll cycle failed: {ex.GetType().Name}: {ex.Message}");
            }
            cycle++;

            try
            {
                bool signalled = await _wake.WaitAsync(PollInterval, ct).ConfigureAwait(false);
                int delay;
                lock (_lock)
                {
                    delay = _pendingDelayMs;
                    rediscover = _pendingRediscover;
                    _pendingDelayMs = -1;
                    _pendingRediscover = false;
                }
                if (signalled && delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CycleAsync(bool rediscover, CancellationToken ct)
    {
        SyncSources();
        IDeviceSource[] sources;
        lock (_lock) sources = [.. _sources];
        foreach (IDeviceSource source in sources)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await source.PollAsync(rediscover, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"source {source.GetType().Name} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Publish();
    }

    /// <summary>Opens sources for newly arrived receivers/headsets and drops the ones whose handle died.</summary>
    private void SyncSources()
    {
        List<HidDeviceInfo> all = HidEnumerator.Enumerate();
        var groups = all
            .Where(d => d.VendorId == LogitechVid && d.Accessible && (d.UsagePage == 0xFF00 || CenturionSource.IsCenturion(d)))
            .GroupBy(d => d.GroupKey)
            .ToDictionary(g => g.Key, g => g.ToList());

        lock (_lock)
        {
            for (int i = _sources.Count - 1; i >= 0; i--)
            {
                IDeviceSource s = _sources[i];
                if (s.Key.Length == 0) continue;
                if (!s.IsDead && groups.ContainsKey(s.Key)) continue;
                _log.Info($"source closed: {s.Key}");
                s.Dispose();
                _sources.RemoveAt(i);
            }

            if (!_sources.Any(s => s is BluetoothSource)) _sources.Add(new BluetoothSource(this));

            foreach ((string key, List<HidDeviceInfo> collections) in groups)
            {
                if (_sources.Any(s => s.Key == key)) continue;
                IDeviceSource? created = collections.Any(CenturionSource.IsCenturion)
                    ? CenturionSource.TryCreate(key, collections, this)
                    : HidPpReceiverSource.TryCreate(key, collections, this);
                if (created != null)
                {
                    _sources.Add(created);
                    _openFailures.Remove(key);
                }
                else if (collections.Any(c => c.UsagePage == 0xFF00 && c.Usage == 0x0002) || collections.Any(CenturionSource.IsCenturion))
                {
                    // Could not open in shared mode (another program holding it exclusively?). Retried every cycle.
                    int n = _openFailures.GetValueOrDefault(key) + 1;
                    _openFailures[key] = n;
                    if (n == 3) _log.Warn($"cannot open {key} after 3 attempts; another program may hold it exclusively");
                }
            }
        }
    }

    private void Publish()
    {
        IReadOnlyList<DeviceSnapshot> list;
        lock (_lock)
        {
            var merged = new Dictionary<string, DeviceSnapshot>();
            foreach (IDeviceSource source in _sources)
            {
                foreach (DeviceSnapshot s in source.Snapshots)
                {
                    // The same device can be reported by two sources (G522 dongle + USB mode): keep the most "present" one.
                    if (!merged.TryGetValue(s.Id, out DeviceSnapshot? existing) || s.Presence < existing.Presence)
                        merged[s.Id] = s;
                }
            }

            foreach ((string id, DeviceSnapshot s) in merged.ToList())
            {
                _known.TryGetValue(id, out DeviceSnapshot? last);
                DeviceSnapshot filled = s;
                if (s.Percent == null && last?.Percent != null) filled = filled with { Percent = last.Percent };
                if (s.LastReadUtc == null && last?.LastReadUtc != null) filled = filled with { LastReadUtc = last.LastReadUtc };
                merged[id] = filled;
                _known[id] = filled;
            }

            foreach ((string id, DeviceSnapshot last) in _known)
            {
                if (!merged.ContainsKey(id))
                    merged[id] = last with { Presence = Presence.Disconnected, Charge = ChargeState.Unknown, Unreliable = false };
            }

            list = Order(merged.Values);
            if (list.SequenceEqual(_current)) return;
            _current = list;
        }
        try { Changed?.Invoke(list); }
        catch (Exception ex) { _log.Warn("Changed handler failed: " + ex.Message); }
    }

    private static List<DeviceSnapshot> Order(IEnumerable<DeviceSnapshot> devices) =>
        [.. devices.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal)];

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _loop?.Wait(1500); } catch (AggregateException) { }
        lock (_lock)
        {
            foreach (IDeviceSource s in _sources) s.Dispose();
            _sources.Clear();
        }
    }
}
