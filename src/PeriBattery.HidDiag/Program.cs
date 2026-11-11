using PeriBattery.Core.Hid;

namespace PeriBattery.HidDiag;

internal static class Program
{
    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        switch (command)
        {
            case "list":
                return ListDevices(args.Contains("--logitech"));
            default:
                Console.WriteLine("Usage: PeriBattery.HidDiag list [--logitech]");
                return 1;
        }
    }

    private static int ListDevices(bool logitechOnly)
    {
        List<HidDeviceInfo> devices = HidEnumerator.Enumerate();
        if (logitechOnly) devices = devices.Where(d => d.VendorId == 0x046D).ToList();

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

    private static string Hex(byte[] ids) => string.Join(" ", ids.Select(b => b.ToString("X2")));
}
