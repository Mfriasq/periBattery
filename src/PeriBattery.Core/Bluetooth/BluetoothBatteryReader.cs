using System.Runtime.InteropServices;
using System.Text;

namespace PeriBattery.Core.Bluetooth;

public sealed record BluetoothBatteryInfo(string InstanceId, string Address, string Name, int Percent, bool Connected);

/// <summary>
/// Reads the battery level Windows stores on Bluetooth LE device nodes (BTHLE\DEV_&lt;MAC&gt;), the same
/// value shown in Settings &gt; Bluetooth. The node (and its last value) survives disconnection, which
/// gives a last-known level for free. XInput and Windows.Gaming.Input are not used because they report
/// bogus levels for Xbox controllers connected over Bluetooth.
/// </summary>
public static class BluetoothBatteryReader
{
    private const uint CR_SUCCESS = 0;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x1;
    private const uint DEVPROP_TYPE_BYTE = 0x03;
    private const uint DEVPROP_TYPE_BOOLEAN = 0x11;
    private const uint DEVPROP_TYPE_STRING = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    private static DEVPROPKEY BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static DEVPROPKEY ConnectedKey = new(new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), 15);
    private static DEVPROPKEY FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static DEVPROPKEY NameKey = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 10);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    /// <summary>Returns every paired Bluetooth LE device that has a stored battery level, connected or not.</summary>
    public static List<BluetoothBatteryInfo> Read()
    {
        var result = new List<BluetoothBatteryInfo>();
        foreach (string id in ListDeviceIds("BTHLE"))
        {
            if (!id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)) continue;
            if (CM_Locate_DevNodeW(out uint devInst, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) continue;

            byte[]? battery = GetProperty(devInst, ref BatteryKey, DEVPROP_TYPE_BYTE);
            if (battery == null || battery.Length < 1 || battery[0] > 100) continue;

            byte[]? connected = GetProperty(devInst, ref ConnectedKey, DEVPROP_TYPE_BOOLEAN);
            string name = GetString(devInst, ref FriendlyNameKey) ?? GetString(devInst, ref NameKey) ?? "Bluetooth";

            // BTHLE\DEV_AC8EBD4E8BD8\...
            string address = id.Length >= 22 ? id.Substring(10, 12).ToUpperInvariant() : id;
            result.Add(new BluetoothBatteryInfo(id, address, name, battery[0], connected is { Length: > 0 } && connected[0] != 0));
        }
        return result;
    }

    private static List<string> ListDeviceIds(string enumerator)
    {
        var ids = new List<string>();
        if (CM_Get_Device_ID_List_SizeW(out uint len, enumerator, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS || len <= 1) return ids;
        char[] buffer = new char[len];
        if (CM_Get_Device_ID_ListW(enumerator, buffer, len, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS) return ids;
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i > start) ids.Add(new string(buffer, start, i - start));
            start = i + 1;
        }
        return ids;
    }

    private static byte[]? GetProperty(uint devInst, ref DEVPROPKEY key, uint expectedType)
    {
        byte[] buffer = new byte[512];
        uint size = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buffer, ref size, 0) != CR_SUCCESS) return null;
        if (type != expectedType) return null;
        return buffer.AsSpan(0, (int)size).ToArray();
    }

    private static string? GetString(uint devInst, ref DEVPROPKEY key)
    {
        byte[]? data = GetProperty(devInst, ref key, DEVPROP_TYPE_STRING);
        if (data == null) return null;
        string s = Encoding.Unicode.GetString(data).TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }
}
