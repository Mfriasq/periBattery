using PeriBattery.Core.Hid;
using PeriBattery.Core.Infra;

namespace PeriBattery.Core.HidPp;

public enum HidPpStatus { Ok, Timeout, Error, ChannelClosed }

/// <summary>Outcome of one HID++ request. <see cref="Params"/> holds the 16 parameter bytes of the reply.</summary>
public readonly record struct HidPpReply(HidPpStatus Status, byte[] Params, byte ErrorCode = 0, bool IsHidPp10Error = false)
{
    public bool Ok => Status == HidPpStatus.Ok;

    /// <summary>Device/receiver busy: HID++ 1.0 code 0x07 or HID++ 2.0 code 0x08.</summary>
    public bool IsBusy => Status == HidPpStatus.Error && (IsHidPp10Error ? ErrorCode == 0x07 : ErrorCode == 0x08);

    /// <summary>Receiver says the device is asleep, off or not paired (HID++ 1.0 codes 0x04, 0x08, 0x09).</summary>
    public bool IsUnreachable => Status == HidPpStatus.Error && IsHidPp10Error && ErrorCode is 0x04 or 0x08 or 0x09;

    public override string ToString() => Status switch
    {
        HidPpStatus.Ok => "ok",
        HidPpStatus.Error => $"error {(IsHidPp10Error ? "1.0" : "2.0")} code 0x{ErrorCode:X2}",
        _ => Status.ToString().ToLowerInvariant(),
    };
}

/// <summary>Counters used to judge whether sharing the receiver with G HUB is hurting reliability.</summary>
public sealed class TransportStats
{
    public int Requests;
    public int Timeouts;
    public int BusyErrors;
    public int ForeignFrames;
    public override string ToString() => $"requests={Requests} timeouts={Timeouts} busy={BusyErrors} foreignFrames={ForeignFrames}";
}

/// <summary>
/// HID++ request/response transport for one receiver. Requests go out as long reports (0x11) on the
/// long collection; the short collection is opened read-only because HID++ 1.0 errors (0x8F) and
/// connection notifications (0x41) arrive there. Replies are matched on device index + feature index +
/// function + our own software ID, so traffic that belongs to G HUB is never consumed as ours.
/// This class has no API to write registers or settings: it can only issue feature calls.
/// </summary>
public sealed class HidPpTransport : IDisposable
{
    public const byte ShortReportId = 0x10;
    public const byte LongReportId = 0x11;
    public const int LongReportLength = 20;

    private readonly HidChannel _long;
    private readonly HidChannel? _short;
    private readonly ILog _log;
    private readonly string _tag;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _pendingLock = new();
    private Pending? _pending;

    private sealed class Pending(byte deviceIndex, byte featureIndex, byte functionSw)
    {
        public readonly byte DeviceIndex = deviceIndex;
        public readonly byte FeatureIndex = featureIndex;
        public readonly byte FunctionSw = functionSw;
        public readonly TaskCompletionSource<HidPpReply> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public HidPpTransport(HidChannel longChannel, HidChannel? shortChannel, byte softwareId, ILog log, string tag)
    {
        _long = longChannel;
        _short = shortChannel;
        SoftwareId = (byte)(softwareId & 0x0F);
        _log = log;
        _tag = tag;
        _long.ReportReceived += OnReport;
        _long.Closed += OnClosed;
        if (_short != null) _short.ReportReceived += OnReport;
        _long.StartReading();
        _short?.StartReading();
    }

    public byte SoftwareId { get; }
    public TransportStats Stats { get; } = new();
    public bool IsClosed => _long.IsClosed;

    /// <summary>Frames that are not replies to our requests: 0x41 connection notifications and feature events (software ID 0).</summary>
    public event Action<byte[]>? Notification;

    public event Action? Closed;

    /// <summary>Sends one feature call, retrying with backoff on BUSY errors and (optionally) on timeouts.</summary>
    public async Task<HidPpReply> RequestAsync(byte deviceIndex, byte featureIndex, byte function, byte[]? parameters = null,
        int timeoutMs = 700, int retries = 2, bool retryOnTimeout = true, CancellationToken ct = default)
    {
        HidPpReply reply = default;
        int delay = 120;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            reply = await RequestOnceAsync(deviceIndex, featureIndex, function, parameters, timeoutMs, ct).ConfigureAwait(false);
            if (reply.Ok || reply.Status == HidPpStatus.ChannelClosed || ct.IsCancellationRequested) return reply;
            bool retryable = reply.IsBusy || (reply.Status == HidPpStatus.Timeout && retryOnTimeout);
            if (!retryable || attempt == retries) return reply;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return reply; }
            delay *= 3;
        }
        return reply;
    }

    private async Task<HidPpReply> RequestOnceAsync(byte deviceIndex, byte featureIndex, byte function, byte[]? parameters,
        int timeoutMs, CancellationToken ct)
    {
        if (_long.IsClosed) return new HidPpReply(HidPpStatus.ChannelClosed, []);

        byte functionSw = (byte)((function << 4) | SoftwareId);
        byte[] frame = new byte[LongReportLength];
        frame[0] = LongReportId;
        frame[1] = deviceIndex;
        frame[2] = featureIndex;
        frame[3] = functionSw;
        if (parameters != null) Buffer.BlockCopy(parameters, 0, frame, 4, Math.Min(parameters.Length, 16));

        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new HidPpReply(HidPpStatus.Timeout, []); }

        var pending = new Pending(deviceIndex, featureIndex, functionSw);
        try
        {
            lock (_pendingLock) _pending = pending;
            Interlocked.Increment(ref Stats.Requests);
            if (_log.DebugEnabled) _log.Debug($"{_tag} TX {frame.Hex()}");

            if (!await _long.WriteAsync(frame, ct).ConfigureAwait(false))
                return new HidPpReply(_long.IsClosed ? HidPpStatus.ChannelClosed : HidPpStatus.Timeout, []);

            Task finished = await Task.WhenAny(pending.Tcs.Task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
            if (finished != pending.Tcs.Task)
            {
                Interlocked.Increment(ref Stats.Timeouts);
                return new HidPpReply(HidPpStatus.Timeout, []);
            }

            HidPpReply reply = await pending.Tcs.Task.ConfigureAwait(false);
            if (reply.IsBusy) Interlocked.Increment(ref Stats.BusyErrors);
            return reply;
        }
        finally
        {
            lock (_pendingLock) if (ReferenceEquals(_pending, pending)) _pending = null;
            _gate.Release();
        }
    }

    private void OnReport(byte[] r)
    {
        if (r.Length < 7 || (r[0] != ShortReportId && r[0] != LongReportId)) return;
        if (_log.DebugEnabled) _log.Debug($"{_tag} RX {r.Hex()}");

        Pending? pending;
        lock (_pendingLock) pending = _pending;

        if (pending != null && r[1] == pending.DeviceIndex)
        {
            // HID++ 2.0 error: [id, idx, 0xFF, featIdx, fn|sw, code]
            if (r[2] == 0xFF && r[3] == pending.FeatureIndex && r[4] == pending.FunctionSw)
            {
                pending.Tcs.TrySetResult(new HidPpReply(HidPpStatus.Error, [], r[5], false));
                return;
            }
            // HID++ 1.0 error: [0x10, idx, 0x8F, featIdx, fn|sw, code, 0]
            if (r[2] == 0x8F && r[3] == pending.FeatureIndex && r[4] == pending.FunctionSw)
            {
                pending.Tcs.TrySetResult(new HidPpReply(HidPpStatus.Error, [], r[5], true));
                return;
            }
            if (r[2] == pending.FeatureIndex && r[3] == pending.FunctionSw)
            {
                byte[] p = new byte[16];
                Buffer.BlockCopy(r, 4, p, 0, Math.Min(r.Length - 4, 16));
                pending.Tcs.TrySetResult(new HidPpReply(HidPpStatus.Ok, p));
                return;
            }
        }

        if (IsForeignReply(r, SoftwareId))
        {
            // A reply addressed to another HID++ client (G HUB, Solaar...). Not ours: leave it alone.
            Interlocked.Increment(ref Stats.ForeignFrames);
            return;
        }

        try { Notification?.Invoke(r); } catch (Exception) { }
    }

    /// <summary>True when the frame is a feature reply or error carrying a non-zero software ID other than ours.</summary>
    internal static bool IsForeignReply(byte[] r, byte ownSoftwareId)
    {
        if (r.Length < 7) return false;
        if (r[2] == 0xFF || r[2] == 0x8F)
        {
            int sw = r[4] & 0x0F;
            return sw != 0 && sw != ownSoftwareId;
        }
        // HID++ 1.0 notifications and register replies use sub-IDs >= 0x40; they are not feature replies.
        if (r[0] == ShortReportId && r[2] >= 0x40) return false;
        int swId = r[3] & 0x0F;
        return swId != 0 && swId != ownSoftwareId;
    }

    private void OnClosed()
    {
        Pending? pending;
        lock (_pendingLock) pending = _pending;
        pending?.Tcs.TrySetResult(new HidPpReply(HidPpStatus.ChannelClosed, []));
        try { Closed?.Invoke(); } catch (Exception) { }
    }

    public void Dispose()
    {
        _long.ReportReceived -= OnReport;
        if (_short != null) _short.ReportReceived -= OnReport;
        _long.Dispose();
        _short?.Dispose();
    }
}
