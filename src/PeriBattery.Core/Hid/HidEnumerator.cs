using System.Text;
using Microsoft.Win32.SafeHandles;
using static PeriBattery.Core.Hid.HidNative;

namespace PeriBattery.Core.Hid;

/// <summary>One top-level HID collection as exposed by Windows.</summary>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength,
    byte[] InputReportIds,
    byte[] OutputReportIds,
    string Product,
    string Manufacturer,
    bool Accessible)
{
    /// <summary>Path of the parent USB interface/device, used to group collections of the same physical device.</summary>
    public string GroupKey
    {
        get
        {
            // \\?\hid#vid_046d&pid_c539&mi_02&col01#a&1fc2b0fa&0&0000#{guid}
            string[] parts = Path.Split('#');
            if (parts.Length < 3) return Path.ToLowerInvariant();
            string hw = parts[1].ToLowerInvariant();
            int col = hw.IndexOf("&col", StringComparison.Ordinal);
            if (col >= 0) hw = hw[..col];
            string inst = parts[2].ToLowerInvariant();
            int last = inst.LastIndexOf('&');
            if (last > 0) inst = inst[..last];
            return hw + "#" + inst;
        }
    }
}

public static class HidEnumerator
{
    /// <summary>Lists every present HID collection. Handles are opened with no access rights (metadata only).</summary>
    public static List<HidDeviceInfo> Enumerate()
    {
        var result = new List<HidDeviceInfo>();
        foreach (string path in GetInterfacePaths())
        {
            HidDeviceInfo? info = Describe(path);
            if (info != null) result.Add(info);
        }
        return result;
    }

    private static List<string> GetInterfacePaths()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        var paths = new List<string>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref hidGuid, null, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len <= 1)
                return paths;
            char[] buffer = new char[len];
            uint cr = CM_Get_Device_Interface_ListW(ref hidGuid, null, buffer, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr == CR_BUFFER_SMALL) continue; // device list changed between the two calls
            if (cr != CR_SUCCESS) return paths;
            int start = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != '\0') continue;
                if (i > start) paths.Add(new string(buffer, start, i - start));
                start = i + 1;
            }
            return paths;
        }
        return paths;
    }

    private static HidDeviceInfo? Describe(string path)
    {
        // Access mask 0: query-only handle. Works even for keyboards/mice that Windows opens exclusively.
        using SafeFileHandle handle = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return new HidDeviceInfo(path, 0, 0, 0, 0, 0, 0, 0, [], [], "", "", false);

        var attrs = new HIDD_ATTRIBUTES { Size = 12 };
        HidD_GetAttributes(handle, ref attrs);

        ushort usagePage = 0, usage = 0;
        int inLen = 0, outLen = 0, featLen = 0;
        byte[] inIds = [], outIds = [];
        if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            try
            {
                ushort[] caps = new ushort[32];
                if (HidP_GetCaps(preparsed, caps) == HIDP_STATUS_SUCCESS)
                {
                    usage = caps[0];
                    usagePage = caps[1];
                    inLen = caps[2];
                    outLen = caps[3];
                    featLen = caps[4];
                    // caps[22..] = NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps,
                    // NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps, ...
                    inIds = ReportIds(preparsed, ReportType.Input, caps[23], caps[24]);
                    outIds = ReportIds(preparsed, ReportType.Output, caps[26], caps[27]);
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        return new HidDeviceInfo(path, attrs.VendorID, attrs.ProductID, usagePage, usage, inLen, outLen, featLen,
            inIds, outIds, ReadString(handle, product: true), ReadString(handle, product: false), true);
    }

    private static byte[] ReportIds(IntPtr preparsed, ReportType type, ushort buttonCaps, ushort valueCaps)
    {
        const int CapsSize = 72;
        var ids = new SortedSet<byte>();
        if (valueCaps > 0)
        {
            byte[] buf = new byte[valueCaps * CapsSize];
            ushort n = valueCaps;
            if (HidP_GetValueCaps(type, buf, ref n, preparsed) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++) ids.Add(buf[i * CapsSize + 2]);
        }
        if (buttonCaps > 0)
        {
            byte[] buf = new byte[buttonCaps * CapsSize];
            ushort n = buttonCaps;
            if (HidP_GetButtonCaps(type, buf, ref n, preparsed) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++) ids.Add(buf[i * CapsSize + 2]);
        }
        return [.. ids];
    }

    private static string ReadString(SafeFileHandle handle, bool product)
    {
        byte[] buf = new byte[512];
        bool ok = product ? HidD_GetProductString(handle, buf, buf.Length) : HidD_GetManufacturerString(handle, buf, buf.Length);
        if (!ok) return "";
        string s = Encoding.Unicode.GetString(buf);
        int end = s.IndexOf('\0');
        return (end >= 0 ? s[..end] : s).Trim();
    }
}
