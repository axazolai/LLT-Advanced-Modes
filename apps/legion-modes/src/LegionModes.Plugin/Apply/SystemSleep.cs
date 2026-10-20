using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LegionModes.Plugin.Apply;

internal static class SystemSleep
{
    const uint TokenAdjustPrivileges = 0x20;
    const uint TokenQuery = 0x8;
    const uint SePrivilegeEnabled = 0x2;
    const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    // Enables SeShutdownPrivilege on the process token. Throws Win32Exception when it cannot be enabled.
    static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue failed");
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges failed");
            var last = Marshal.GetLastWin32Error();
            if (last == ErrorNotAllAssigned)
                throw new Win32Exception(last, "SeShutdownPrivilege is not held by this process");
        }
        finally { CloseHandle(token); }
    }

    // null = suspend requested; otherwise the failure text. Never throws.
    public static Task<string?> SuspendAsync() => Task.Run<string?>(() =>
    {
        try
        {
            EnableShutdownPrivilege();
            return Application.SetSuspendState(PowerState.Suspend, true, false) ? null : "SetSuspendState returned false";
        }
        catch (Exception e)
        {
            Log.Error("sleep now", e);
            return e.Message;
        }
    });
}
