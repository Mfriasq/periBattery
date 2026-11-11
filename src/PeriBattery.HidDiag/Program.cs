using PeriBattery.Core.Centurion;
using PeriBattery.Core.Devices;
using PeriBattery.Core.Hid;
using PeriBattery.Core.HidPp;
using PeriBattery.Core.Infra;

namespace PeriBattery.HidDiag;

internal static class Program
{
    private const ushort LogitechVid = 0x046D;
    private const byte SoftwareId = 0x0E;

    private static async Task<int> Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        bool raw = args.Contains("--raw");
        switch (command)
        {
            case "list":
                return ListDevices(args.Contains("--logitech"));
            case "hidpp":
                await ProbeHidPpAsync(new ConsoleLog(raw));
                return 0;
            case "centurion":
                await ExploreCenturionAsync(new ConsoleLog(true));
                return 0;
            case "watch":
                await WatchAsync(new ConsoleLog(raw), IntArg(args, "--seconds", 15), IntArg(args, "--interval", 60));
                return 0;
            default:
                Console.WriteLine("Usage: PeriBattery.HidDiag <command>\n" +
                                  "  list [--logitech]                          list HID collections\n" +
                                  "  hidpp [--raw]                              probe HID++ receivers (read-only)\n" +
                                  "  centurion                                  probe Centurion headsets with raw dumps (read-only)\n" +
                                  "  watch [--seconds N] [--interval N] [--raw] run the same device manager as the app");
                return 1;
        }
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
    }

    /// <summary>Runs the real <see cref="DeviceManager"/> for a while and prints every state change plus final statistics.</summary>
    private static async Task WatchAsync(ILog log, int seconds, int intervalSeconds)
    {
        using var manager = new DeviceManager(log, SoftwareId) { PollInterval = TimeSpan.FromSeconds(intervalSeconds) };
        int updates = 0;
        manager.Changed += list =>
        {
            updates++;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] update #{updates}");
            foreach (DeviceSnapshot d in list)
            {
                Console.WriteLine($"   {d.Name,-26} {d.Kind,-9} {d.Link,-10} {d.Presence,-12} " +
                                  $"{(d.Percent.HasValue ? d.Percent + "%" : "?"),5} {d.Charge,-12}{(d.Unreliable ? " UNRELIABLE" : "")}");
            }
        };
        manager.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"done: {updates} updates in {seconds} s");
        foreach (string line in manager.Diagnostics) Console.WriteLine("   " + line);
    }

    private static int ListDevices(bool logitechOnly)
    {
        List<HidDeviceInfo> devices = HidEnumerator.Enumerate();
        if (logitechOnly) devices = devices.Where(d => d.VendorId == LogitechVid).ToList();

        Console.WriteLine($"{devices.Count} HID collections found\n");
        foreach (var group in devices.GroupBy(d => d.GroupKey).OrderBy(g => g.First().VendorId).ThenBy(g => g.First().ProductId))
        {
            HidDeviceInfo first = group.First();
            Console.WriteLine($"VID {first.VendorId:X4}  PID {first.ProductId:X4}  \"{first.Product}\"  [{first.Manufacturer}]");
            foreach (HidDeviceInfo d in group.OrderBy(d => d.Path))
            {
                string access = d.Accessible ? "" : "  (not accessible)";
                Console.WriteLine($"    usagePage 0x{d.UsagePage:X4}  usage 0x{d.Usage:X4}  in {d.InputReportLength,3}  out {d.OutputReportLength,3}  feat {d.FeatureReportLength,3}" +
                                  $"  inIDs [{Hex(d.InputReportIds)}]  outIDs [{Hex(d.OutputReportIds)}]{access}");
                Console.WriteLine($"        {d.Path}");
            }
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>Pings device indexes 1..6 on every HID++ receiver and reads the battery of whatever answers.</summary>
    private static async Task ProbeHidPpAsync(ILog log)
    {
        var groups = HidEnumerator.Enumerate()
            .Where(d => d.VendorId == LogitechVid && d.UsagePage == 0xFF00)
            .GroupBy(d => d.GroupKey);

        foreach (var group in groups)
        {
            HidDeviceInfo? longInfo = group.FirstOrDefault(d => d.Usage == 0x0002);
            HidDeviceInfo? shortInfo = group.FirstOrDefault(d => d.Usage == 0x0001);
            if (longInfo == null) continue;

            log.Info($"\n== Receiver {longInfo.VendorId:X4}:{longInfo.ProductId:X4} \"{longInfo.Product}\"");
            HidChannel? longCh = HidChannel.TryOpen(longInfo, writable: true, log);
            if (longCh == null) continue;
            HidChannel? shortCh = shortInfo != null ? HidChannel.TryOpen(shortInfo, writable: false, log) : null;

            using var transport = new HidPpTransport(longCh, shortCh, SoftwareId, log, $"{longInfo.ProductId:X4}");
            transport.Notification += f => log.Info($"   notification: {f.Hex()}");

            for (byte index = 1; index <= 6; index++)
            {
                var device = new HidPpDevice(transport, index);
                HidPpReply ping = await device.PingAsync(timeoutMs: 400, retries: 0);
                if (!ping.Ok)
                {
                    log.Info($"   index {index}: {ping}");
                    continue;
                }

                await device.ReadNameAsync();
                await device.ResolveBatteryFeatureAsync();
                (HidPpReply reply, var reading) = await device.ReadBatteryAsync();
                log.Info($"   index {index}: HID++ {device.Protocol}  name \"{device.Name}\"  type {device.DeviceType}  " +
                         $"battery feature 0x{device.BatteryFeatureId:X4} @ index {device.BatteryFeatureIndex}");
                log.Info($"            battery: {(reading != null ? reading.ToString() : reply.ToString())}  raw [{reply.Params.Hex(4)}]");
            }
            log.Info($"   stats: {transport.Stats}");
        }
    }

    /// <summary>Exploratory, read-only probe of every Centurion (0xFFA0) collection with full raw dumps.</summary>
    private static async Task ExploreCenturionAsync(ILog log)
    {
        foreach (HidDeviceInfo info in HidEnumerator.Enumerate().Where(d => d.VendorId == LogitechVid && d.UsagePage == 0xFFA0))
        {
            log.Info($"\n== {info.VendorId:X4}:{info.ProductId:X4} \"{info.Product}\"");
            HidChannel? channel = HidChannel.TryOpen(info, writable: true, log);
            if (channel == null) continue;
            using var t = new CenturionTransport(channel, SoftwareId, log, $"{info.ProductId:X4}");

            log.Info("-- passive listen 800 ms");
            await Task.Delay(800);

            log.Info("-- direct Root.getFeature(0x0003 bridge)");
            CenturionReply bridge = await t.RequestAsync(0x00, 0, [0x00, 0x03]);
            log.Info($"   => {bridge} [{bridge.Data.Hex(8)}]");

            log.Info("-- direct Root.getFeature(0x0104 battery)");
            CenturionReply direct = await t.RequestAsync(0x00, 0, [0x01, 0x04]);
            log.Info($"   => {direct} [{direct.Data.Hex(8)}]");
            if (direct.Ok && direct.Data[0] != 0)
            {
                log.Info($"-- direct battery get (index {direct.Data[0]})");
                CenturionReply b = await t.RequestAsync(direct.Data[0], 0);
                log.Info($"   => {b} [{b.Data.Hex(8)}]");
            }

            if (bridge.Ok && bridge.Data[0] != 0)
            {
                byte bi = bridge.Data[0];
                log.Info($"-- bridge status (index {bi}, fn 0)");
                CenturionReply st = await t.RequestAsync(bi, 0);
                log.Info($"   => {st} [{st.Data.Hex(8)}]");

                log.Info("-- bridged Root.getFeature(0x0104 battery)");
                CenturionReply bf = await t.BridgeRequestAsync(bi, 0x00, 0, [0x01, 0x04]);
                log.Info($"   => {bf} [{bf.Data.Hex(8)}]");
                if (bf.Ok && bf.Data[0] != 0)
                {
                    log.Info($"-- bridged battery get (index {bf.Data[0]})");
                    CenturionReply b = await t.BridgeRequestAsync(bi, bf.Data[0], 0);
                    log.Info($"   => {b} [{b.Data.Hex(8)}]");
                }
            }
            await Task.Delay(300);
        }
    }

    private static string Hex(byte[] ids) => string.Join(" ", ids.Select(b => b.ToString("X2")));
}
