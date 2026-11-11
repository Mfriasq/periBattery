using Microsoft.Win32.SafeHandles;
using PeriBattery.Core.Infra;
using static PeriBattery.Core.Hid.HidNative;

namespace PeriBattery.Core.Hid;

/// <summary>
/// Shared-mode, overlapped handle to one HID collection. Never opened exclusively, so G HUB (or any
/// other client) keeps full access to the same collection.
/// </summary>
public sealed class HidChannel : IDisposable
{
    private readonly FileStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _closed;

    private HidChannel(HidDeviceInfo info, FileStream stream, bool writable)
    {
        Info = info;
        _stream = stream;
        Writable = writable;
    }

    public HidDeviceInfo Info { get; }
    public bool Writable { get; }
    public bool IsClosed => _closed != 0;

    /// <summary>Raised on a thread-pool thread for every input report (report ID at index 0).</summary>
    public event Action<byte[]>? ReportReceived;

    /// <summary>Raised once when the handle dies (device unplugged, I/O error) or the channel is disposed.</summary>
    public event Action? Closed;

    public static HidChannel? TryOpen(HidDeviceInfo info, bool writable, ILog log)
    {
        uint access = GENERIC_READ | (writable ? GENERIC_WRITE : 0);
        SafeFileHandle handle = CreateFileW(info.Path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            log.Warn($"Cannot open HID collection (win32 error {error}): {info.Path}");
            handle.Dispose();
            return null;
        }

        try
        {
            var stream = new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read, bufferSize: 0, isAsync: true);
            return new HidChannel(info, stream, writable);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            log.Warn($"Cannot wrap HID handle: {ex.Message}");
            handle.Dispose();
            return null;
        }
    }

    public void StartReading() => _ = Task.Run(ReadLoopAsync);

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[Math.Max(Info.InputReportLength, 1)];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                byte[] report = new byte[n];
                Buffer.BlockCopy(buffer, 0, report, 0, n);
                try { ReportReceived?.Invoke(report); }
                catch (Exception) { /* a faulty subscriber must not kill the read loop */ }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        {
        }
        MarkClosed();
    }

    /// <summary>Writes one output report, zero-padded to the collection's output report length.</summary>
    public async Task<bool> WriteAsync(byte[] report, CancellationToken ct)
    {
        if (!Writable || IsClosed) return false;
        byte[] padded = report;
        if (report.Length != Info.OutputReportLength)
        {
            padded = new byte[Info.OutputReportLength];
            Buffer.BlockCopy(report, 0, padded, 0, Math.Min(report.Length, padded.Length));
        }

        try
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }

        try
        {
            await _stream.WriteAsync(padded, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            MarkClosed();
            return false;
        }
        finally
        {
            try { _writeGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { Closed?.Invoke(); } catch (Exception) { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream.Dispose(); } catch (IOException) { }
        MarkClosed();
    }
}
