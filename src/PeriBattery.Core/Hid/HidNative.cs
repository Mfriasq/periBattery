using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeriBattery.Core.Hid;

/// <summary>P/Invoke surface for HID enumeration and shared-mode I/O. No third-party HID library is used.</summary>
internal static class HidNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public const uint CR_SUCCESS = 0;
    public const uint CR_BUFFER_SMALL = 0x1A;
    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    public enum ReportType { Input = 0, Output = 1, Feature = 2 }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("hid.dll")]
    public static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle handle, byte[] buffer, int bufferLength);

    // HIDP_CAPS is 32 consecutive USHORTs; read it as a flat array.
    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsed, [Out] ushort[] caps);

    // HIDP_VALUE_CAPS and HIDP_BUTTON_CAPS are both 72 bytes with ReportID at offset 2.
    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(ReportType type, [Out] byte[] caps, ref ushort count, IntPtr preparsed);

    [DllImport("hid.dll")]
    public static extern int HidP_GetButtonCaps(ReportType type, [Out] byte[] caps, ref ushort count, IntPtr preparsed);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid classGuid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_Interface_ListW(ref Guid classGuid, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
