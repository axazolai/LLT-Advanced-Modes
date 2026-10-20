using System.Runtime.InteropServices;

namespace LegionModes.Plugin.Status;

public enum CameraState { Present, Absent, Unknown }

// Camera presence: a read of the present devices of the Camera and Image setup classes (SetupAPI), and device-interface
// notifications (CfgMgr32) that say when to read again.
internal sealed class CameraDevice : IDisposable
{
    static readonly Guid CameraClass = new("ca3e7ab9-b4c3-4ae6-8251-579ef933890f");
    static readonly Guid ImageClass = new("6bdd1fc6-810f-11d0-bec7-08002be2092f");
    // Device interfaces: KSCATEGORY_VIDEO_CAMERA, KSCATEGORY_SENSOR_CAMERA, GUID_DEVINTERFACE_IMAGE.
    static readonly Guid[] Interfaces =
    [
        new("e5323777-f976-4f5b-9b55-b94699c46e44"),
        new("24e552d7-6523-47f7-a647-d3465bf1f5ca"),
        new("6bdd1fc6-810f-11d0-bec7-08002be2092f"),
    ];

    const uint DigcfPresent = 0x2;
    static readonly IntPtr InvalidHandle = new(-1);

    readonly List<IntPtr> _registrations = [];
    readonly NotifyCallback _callback;   // rooted: the native side keeps the pointer
    readonly Action _changed;
    int _disposed;

    public CameraDevice(Action changed)
    {
        _changed = changed;
        _callback = OnNotify;
    }

    const string BuiltIn = @"USB\VID_30C9&PID_00AC";
    const uint DigcfAllClasses = 0x4;
    const uint SpdrpHardwareId = 1;

    // Present when a Camera-class device is present, or the built-in camera's USB device is; Image-class devices (scanners,
    // phones in PTP mode) count only when no Camera-class device exists. Unknown when the device list cannot be read.
    public static CameraState Read()
    {
        try
        {
            if (AnyPresent(CameraClass) is not { } camera) return CameraState.Unknown;
            if (camera || BuiltInPresent()) return CameraState.Present;
            if (AnyPresent(ImageClass) is not { } image) return CameraState.Unknown;
            return image ? CameraState.Present : CameraState.Absent;
        }
        catch (Exception e)
        {
            Log.Error("read camera devices", e);
            return CameraState.Unknown;
        }
    }

    static bool? AnyPresent(Guid cls)
    {
        var g = cls;
        var set = SetupDiGetClassDevsW(ref g, null, IntPtr.Zero, DigcfPresent);
        if (set == InvalidHandle) return null;
        try
        {
            var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            return SetupDiEnumDeviceInfo(set, 0, ref data);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // Present USB devices whose hardware ID starts with the built-in camera's VID/PID.
    static bool BuiltInPresent()
    {
        var set = SetupDiGetClassDevsAllW(IntPtr.Zero, "USB", IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == InvalidHandle) return false;
        try
        {
            var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            var buffer = new byte[2048];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, out _, buffer, (uint)buffer.Length, out var size)) continue;
                var ids = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (ids.Any(id => id.StartsWith(BuiltIn, StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // Returns false when no registration succeeded (the caller then relies on its periodic read).
    public bool Watch()
    {
        foreach (var guid in Interfaces)
        {
            try
            {
                var filter = new NotifyFilter { CbSize = 416, Flags = 0, FilterType = 0, Reserved = 0, ClassGuid = guid };
                var rc = CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out var handle);
                if (rc == 0) _registrations.Add(handle);
                else Log.Trace($"camera notification {guid}: CM_Register_Notification returned {rc}");
            }
            catch (Exception e) { Log.Error("register camera notification", e); }
        }
        return _registrations.Count > 0;
    }

    uint OnNotify(IntPtr notify, IntPtr context, int action, IntPtr eventData, uint eventDataSize)
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0) _changed();
        }
        catch (Exception e) { Log.Error("camera notification", e); }
        return 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        foreach (var h in _registrations)
        {
            try { CM_Unregister_Notification(h); } catch (Exception e) { Log.Error("unregister camera notification", e); }
        }
        _registrations.Clear();
    }

    delegate uint NotifyCallback(IntPtr notify, IntPtr context, int action, IntPtr eventData, uint eventDataSize);

    [StructLayout(LayoutKind.Explicit, Size = 416)]
    struct NotifyFilter
    {
        [FieldOffset(0)] public uint CbSize;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public int FilterType;     // CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    static extern IntPtr SetupDiGetClassDevsAllW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, uint property,
        out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    static extern uint CM_Register_Notification(ref NotifyFilter filter, IntPtr context, NotifyCallback callback, out IntPtr notifyContext);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    static extern uint CM_Unregister_Notification(IntPtr notifyContext);
}
