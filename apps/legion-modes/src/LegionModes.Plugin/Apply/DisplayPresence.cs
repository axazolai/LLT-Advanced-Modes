using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LegionModes.Plugin.Apply;

// "An external display is connected" = at least one available non-internal target among ALL display paths. Unlike
// Display.GetDisplays (attached displays only) it still sees a monitor that an InternalOnly projection detached.
internal static class DisplayPresence
{
    const uint QdcAllPaths = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathTargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public int OutputTechnology; public uint Rotation;
        public uint Scaling; public uint RefreshNumerator; public uint RefreshDenominator; public uint ScanLineOrdering;
        public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo { public PathSourceInfo Source; public PathTargetInfo Target; public uint Flags; }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] byte[] modes, IntPtr topologyId);

    const int Internal = unchecked((int)0x80000000);
    const int Lvds = 6, DisplayPortEmbedded = 11, UdiEmbedded = 13;

    internal static bool IsInternal(int outputTechnology) =>
        outputTechnology is Internal or Lvds or DisplayPortEmbedded or UdiEmbedded;

    // Throws Win32Exception when the query fails.
    public static bool ExternalConnected()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var rc = GetDisplayConfigBufferSizes(QdcAllPaths, out var np, out var nm);
            if (rc != 0) throw new Win32Exception(rc, "GetDisplayConfigBufferSizes failed");
            var paths = new PathInfo[np];
            var modes = new byte[nm * 64];   // DISPLAYCONFIG_MODE_INFO is 64 bytes
            rc = QueryDisplayConfig(QdcAllPaths, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (rc == 122) continue;   // ERROR_INSUFFICIENT_BUFFER: the set changed between the two calls
            if (rc != 0) throw new Win32Exception(rc, "QueryDisplayConfig failed");
            for (var i = 0; i < np; i++)
                if (paths[i].Target.TargetAvailable != 0 && !IsInternal(paths[i].Target.OutputTechnology)) return true;
            return false;
        }
        throw new InvalidOperationException("QueryDisplayConfig: buffer size kept changing");
    }
}
