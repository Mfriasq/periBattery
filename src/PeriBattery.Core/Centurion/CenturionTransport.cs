using PeriBattery.Core.Hid;
using PeriBattery.Core.HidPp;
using PeriBattery.Core.Infra;

namespace PeriBattery.Core.Centurion;

/// <summary>Outcome of one Centurion request. <see cref="Data"/> is the payload that follows the function byte.</summary>
public readonly record struct CenturionReply(HidPpStatus Status, byte[] Data, byte ErrorCode = 0)
{
    public bool Ok => Status == HidPpStatus.Ok;
    public override string ToString() => Status == HidPpStatus.Error ? $"error code 0x{ErrorCode:X2}" : Status.ToString().ToLowerInvariant();
}

/// <summary>
/// "Centurion" transport used by recent Logitech headsets (G522, PRO X 2...) on usage page 0xFFA0.
/// Frame (report 0x50 variant, 64 bytes), as documented in Solaar base.py and HeadsetControl:
///   [0x50, deviceAddr, cplLength, flags, featureIndex, (function &lt;&lt; 4) | softwareId, params...]
/// where cplLength counts the bytes from flags to the end of the meaningful data.
/// Calls to the headset behind a wireless dongle are wrapped in the dongle's bridge feature (0x0003):
///   params = [(deviceId &lt;&lt; 4) | lenHi, lenLo, 0x00, subFeatureIndex, (subFunction &lt;&lt; 4) | softwareId, subParams...]
/// and the answer comes back as a bridge event (software ID 0) carrying the same sub-message layout.
/// Only feature look-ups and getters are ever sent.
/// </summary>
public sealed class CenturionTransport : IDisposable
{
    public const byte ReportId = 0x50;
    public const int FrameSize = 64;
    private const byte BridgeSendFunction = 1;

    private readonly HidChannel _channel;
    private readonly ILog _log;
    private readonly string _tag;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _pendingLock = new();
    private Pending? _pending;

    private sealed class Pending(Func<byte[], CenturionReply?> matcher)
    {
        public readonly Func<byte[], CenturionReply?> Matcher = matcher;
        public readonly TaskCompletionSource<CenturionReply> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public CenturionTransport(HidChannel channel, byte softwareId, ILog log, string tag)
    {
        _channel = channel;
        SoftwareId = (byte)(softwareId & 0x0F);
        _log = log;
        _tag = tag;
        _channel.ReportReceived += OnReport;
        _channel.Closed += OnClosed;
        _channel.StartReading();
    }

    public byte SoftwareId { get; }

    /// <summary>Device address byte; 0x23 for the G522 according to Solaar and HeadsetControl.</summary>
    public byte Address { get; set; } = 0x23;

    public TransportStats Stats { get; } = new();
    public bool IsClosed => _channel.IsClosed;

    /// <summary>Frames that did not answer a pending request (events, other clients' traffic).</summary>
    public event Action<byte[]>? Notification;
    public event Action? Closed;

    /// <summary>Feature call addressed to the device that owns this HID interface (dongle, or the headset itself when wired).</summary>
    public Task<CenturionReply> RequestAsync(byte featureIndex, byte function, byte[]? parameters = null,
        int timeoutMs = 700, int retries = 1, CancellationToken ct = default)
    {
        byte functionSw = (byte)((function << 4) | SoftwareId);
        byte[] frame = BuildFrame(Address, featureIndex, functionSw, parameters ?? []);
        byte address = Address;
        return SendAsync(frame, r => MatchDirect(r, address, featureIndex, functionSw), timeoutMs, retries, ct);
    }

    /// <summary>Feature call forwarded to the headset through the dongle's bridge feature.</summary>
    public Task<CenturionReply> BridgeRequestAsync(byte bridgeIndex, byte subFeatureIndex, byte subFunction, byte[]? subParameters = null,
        int timeoutMs = 1200, int retries = 1, CancellationToken ct = default)
    {
        subParameters ??= [];
        byte subFunctionSw = (byte)((subFunction << 4) | SoftwareId);
        int subLength = 3 + subParameters.Length;
        byte[] p = new byte[2 + subLength];
        p[0] = (byte)((subLength >> 8) & 0x0F); // device id 0 (headset) in the high nibble
        p[1] = (byte)subLength;
        p[2] = 0x00;
        p[3] = subFeatureIndex;
        p[4] = subFunctionSw;
        Buffer.BlockCopy(subParameters, 0, p, 5, subParameters.Length);

        byte bridgeFunctionSw = (byte)((BridgeSendFunction << 4) | SoftwareId);
        byte[] frame = BuildFrame(Address, bridgeIndex, bridgeFunctionSw, p);
        byte address = Address;
        return SendAsync(frame, r => MatchBridge(r, address, bridgeIndex, bridgeFunctionSw, subFeatureIndex, subFunctionSw), timeoutMs, retries, ct);
    }

    internal static byte[] BuildFrame(byte address, byte featureIndex, byte functionSw, byte[] parameters)
    {
        byte[] frame = new byte[FrameSize];
        frame[0] = ReportId;
        frame[1] = address;
        frame[2] = (byte)(3 + parameters.Length); // flags + feature index + function byte + params
        frame[3] = 0x00;                          // single-fragment frame
        frame[4] = featureIndex;
        frame[5] = functionSw;
        Buffer.BlockCopy(parameters, 0, frame, 6, Math.Min(parameters.Length, FrameSize - 6));
        return frame;
    }

    internal static CenturionReply? MatchDirect(byte[] r, byte address, byte featureIndex, byte functionSw)
    {
        if (r.Length < 8 || r[0] != ReportId || r[1] != address) return null;
        if (r[4] == 0xFF && r[5] == featureIndex && r[6] == functionSw)
            return new CenturionReply(HidPpStatus.Error, [], r[7]);
        if (r[4] != featureIndex || r[5] != functionSw) return null;
        return new CenturionReply(HidPpStatus.Ok, Payload(r, 6));
    }

    internal static CenturionReply? MatchBridge(byte[] r, byte address, byte bridgeIndex, byte bridgeFunctionSw, byte subFeatureIndex, byte subFunctionSw)
    {
        if (r.Length < 12 || r[0] != ReportId || r[1] != address) return null;
        // The dongle rejected the bridge call itself.
        if (r[4] == 0xFF && r[5] == bridgeIndex && r[6] == bridgeFunctionSw)
            return new CenturionReply(HidPpStatus.Error, [], r[7]);
        // Bridge "message from headset" event: function 1, software ID 0. (The frame with our own
        // software ID is just the dongle's acknowledgement and carries no answer.)
        if (r[4] != bridgeIndex || r[5] != (BridgeSendFunction << 4)) return null;
        if (r[9] == 0xFF && r[10] == subFeatureIndex && r[11] == subFunctionSw)
            return new CenturionReply(HidPpStatus.Error, [], r.Length > 12 ? r[12] : (byte)0);
        if (r[9] != subFeatureIndex || r[10] != subFunctionSw) return null;
        return new CenturionReply(HidPpStatus.Ok, Payload(r, 11));
    }

    private static byte[] Payload(byte[] r, int offset)
    {
        byte[] data = new byte[Math.Max(0, r.Length - offset)];
        Buffer.BlockCopy(r, offset, data, 0, data.Length);
        return data;
    }

    private async Task<CenturionReply> SendAsync(byte[] frame, Func<byte[], CenturionReply?> matcher, int timeoutMs, int retries, CancellationToken ct)
    {
        CenturionReply reply = default;
        int delay = 150;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            reply = await SendOnceAsync(frame, matcher, timeoutMs, ct).ConfigureAwait(false);
            if (reply.Status != HidPpStatus.Timeout || attempt == retries || ct.IsCancellationRequested) return reply;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return reply; }
            delay *= 3;
        }
        return reply;
    }

    private async Task<CenturionReply> SendOnceAsync(byte[] frame, Func<byte[], CenturionReply?> matcher, int timeoutMs, CancellationToken ct)
    {
        if (_channel.IsClosed) return new CenturionReply(HidPpStatus.ChannelClosed, []);
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new CenturionReply(HidPpStatus.Timeout, []); }

        var pending = new Pending(matcher);
        try
        {
            lock (_pendingLock) _pending = pending;
            Interlocked.Increment(ref Stats.Requests);
            if (_log.DebugEnabled) _log.Debug($"{_tag} TX {frame.Hex()}");

            if (!await _channel.WriteAsync(frame, ct).ConfigureAwait(false))
                return new CenturionReply(_channel.IsClosed ? HidPpStatus.ChannelClosed : HidPpStatus.Timeout, []);

            Task finished = await Task.WhenAny(pending.Tcs.Task, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
            if (finished != pending.Tcs.Task)
            {
                Interlocked.Increment(ref Stats.Timeouts);
                return new CenturionReply(HidPpStatus.Timeout, []);
            }
            return await pending.Tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingLock) if (ReferenceEquals(_pending, pending)) _pending = null;
            _gate.Release();
        }
    }

    private void OnReport(byte[] r)
    {
        if (_log.DebugEnabled) _log.Debug($"{_tag} RX {r.Hex()}");
        Pending? pending;
        lock (_pendingLock) pending = _pending;
        if (pending != null)
        {
            CenturionReply? reply = pending.Matcher(r);
            if (reply.HasValue)
            {
                pending.Tcs.TrySetResult(reply.Value);
                return;
            }
        }
        try { Notification?.Invoke(r); } catch (Exception) { }
    }

    private void OnClosed()
    {
        Pending? pending;
        lock (_pendingLock) pending = _pending;
        pending?.Tcs.TrySetResult(new CenturionReply(HidPpStatus.ChannelClosed, []));
        try { Closed?.Invoke(); } catch (Exception) { }
    }

    public void Dispose()
    {
        _channel.ReportReceived -= OnReport;
        _channel.Dispose();
    }
}
