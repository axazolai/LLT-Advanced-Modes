using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LegionModes.Core;
using LegionModes.Core.Status;

namespace LegionModes.Guard;

// The side effects the policy asks for. In console mode nothing sleeps the machine or starts the keeper directly.
internal sealed class GuardActions(bool console)
{
    const uint NoSession = 0xFFFFFFFF;
    const string KeeperProcess = "LegionModes.Keeper";
    static readonly TimeSpan Debounce = TimeSpan.FromMinutes(10);

    readonly Dictionary<string, DateTimeOffset> _lastWarned = [];

    public bool Console { get; } = console;

    static uint ConsoleSession() => Native.WTSGetActiveConsoleSessionId();

    // A user is logged on at the console (the lock screen counts: the keeper must be up before the unlock).
    public bool UserSessionActive()
    {
        var sid = ConsoleSession();
        if (sid == NoSession) return false;
        if (!Native.WTSQuerySessionInformation(IntPtr.Zero, sid, 5 /* WTSUserName */, out var buf, out _)) return false;
        try { return !string.IsNullOrEmpty(Marshal.PtrToStringUni(buf)); }
        finally { Native.WTSFreeMemory(buf); }
    }

    public bool KeeperRunning()
    {
        var sid = ConsoleSession();
        var found = false;
        foreach (var p in Process.GetProcessesByName(KeeperProcess))
        {
            try { if ((uint)p.SessionId == sid) found = true; }
            catch { /* the process ended or is not readable: not counted */ }
            finally { p.Dispose(); }
        }
        return found;
    }

    // SetSuspendState under LocalSystem needs SeShutdownPrivilege enabled; the service token has it disabled.
    public static void EnableShutdownPrivilege()
    {
        IntPtr token = 0;
        try
        {
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x28 /* ADJUST_PRIVILEGES | QUERY */, out token))
                throw new Win32Exception();
            if (!Native.LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) throw new Win32Exception();
            var tp = new Native.TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            Native.AdjustTokenPrivileges(token, false, ref tp, Marshal.SizeOf<Native.TOKEN_PRIVILEGES>(), IntPtr.Zero, IntPtr.Zero);
            var err = Marshal.GetLastWin32Error();   // 0 = enabled, 1300 = ERROR_NOT_ALL_ASSIGNED
            GuardLog.Write(err == 0 ? "SeShutdownPrivilege enabled" : $"SeShutdownPrivilege NOT enabled (error {err}); sleeping will fail");
        }
        catch (Exception e) { GuardLog.Write("SeShutdownPrivilege failed: " + e.Message); }
        finally { if (token != 0) Native.CloseHandle(token); }
    }

    public void RunKeeperTask()
    {
        Record("running the keeper task", "keeper missing; running the logon task");
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Run /TN \"{Paths.KeeperTask}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } GuardLog.Write("schtasks timed out"); return; }
            GuardLog.Write($"schtasks exit {p.ExitCode}: {(p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim()}");
        }
        catch (Exception e) { GuardLog.Write("schtasks failed: " + e.Message); }
    }

    public void StartKeeperDirect()
    {
        if (Console) { GuardLog.Write("console mode: would start keeper directly"); return; }
        Record("starting the keeper directly", "keeper missing and the task did not bring it back; started it directly");
        IntPtr user = 0, linked = 0, primary = 0, env = 0;
        try
        {
            if (!Native.WTSQueryUserToken(ConsoleSession(), out user)) throw new Win32Exception();
            var source = user;
            // Limited token of an admin user -> its linked (full) token; already full or a standard user -> as is.
            if (Native.GetTokenInformation(user, 18 /* TokenElevationType */, out int elevation, sizeof(int), out _)
                && elevation == 3 /* TokenElevationTypeLimited */
                && Native.GetTokenInformation(user, 19 /* TokenLinkedToken */, out linked, IntPtr.Size, out _)) source = linked;
            if (!Native.DuplicateTokenEx(source, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out primary))
                throw new Win32Exception();
            if (!Native.CreateEnvironmentBlock(out var userEnv, primary, false)) throw new Win32Exception();
            try { env = FilterEnvironment(userEnv); }
            finally { Native.DestroyEnvironmentBlock(userEnv); }
            var exe = Path.Combine(Paths.InstallDir, "LegionModes.Keeper.exe");
            var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
            try
            {
                var si = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>(), lpDesktop = desktop };
                if (!Native.CreateProcessAsUser(primary, exe, new StringBuilder("\"" + exe + "\""), IntPtr.Zero, IntPtr.Zero, false,
                        0x400 /* CREATE_UNICODE_ENVIRONMENT */, env, Paths.InstallDir, ref si, out var pi))
                    throw new Win32Exception();
                Native.CloseHandle(pi.hThread);
                Native.CloseHandle(pi.hProcess);
                GuardLog.Write($"keeper started directly, pid {pi.dwProcessId}");
            }
            finally { Marshal.FreeHGlobal(desktop); }
        }
        catch (Exception e) { GuardLog.Write("direct keeper start failed: " + e.Message); }
        finally
        {
            if (env != 0) Marshal.FreeHGlobal(env);
            foreach (var h in new[] { primary, linked, user }) if (h != 0) Native.CloseHandle(h);
        }
    }

    // The user's block without runtime-injection variables: the keeper runs elevated.
    internal static IntPtr FilterEnvironment(IntPtr block)
    {
        var kept = new StringBuilder();
        for (var p = block; ;)
        {
            var entry = Marshal.PtrToStringUni(p) ?? "";
            if (entry.Length == 0) break;
            p += (entry.Length + 1) * 2;
            if (entry.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || entry.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase)
                || entry.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)) continue;
            kept.Append(entry).Append('\0');
        }
        kept.Append('\0');
        if (kept.Length == 1) kept.Append('\0');
        return Marshal.StringToHGlobalUni(kept.ToString());
    }

    public void SleepNow()
    {
        if (Console) { GuardLog.Write("console mode: would sleep (lid closed, plugin silent)"); return; }
        Record("putting the laptop to sleep", "lid closed and the plugin is silent; putting the laptop to sleep");
        try
        {
            if (Native.SetSuspendState(0, 0, 0) == 0)
            {
                var msg = "sleep failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                GuardLog.Write(msg);
                Warn("sleep-failed", msg);
            }
        }
        catch (Exception e) { GuardLog.Write("sleep failed: " + e.Message); }
    }

    // guard.log always; the event log and the pending notification once per key per 10 minutes.
    void Record(string log, string notification)
    {
        GuardLog.Write(log);
        Warn(log, notification);
    }

    public void Warn(string key, string text)
    {
        var now = DateTimeOffset.Now;
        if (_lastWarned.TryGetValue(key, out var last) && now - last < Debounce) return;
        _lastWarned[key] = now;
        if (!Console)
        {
            try { EventLog.WriteEntry(Paths.PluginId, "guard: " + text, EventLogEntryType.Warning); }
            catch (Exception e) { GuardLog.Write("event log failed: " + e.Message); }
        }
        try { PendingNotifications.Append(Path.Combine(Paths.DataRoot(), "notifications-pending.json"), $"{now:u} guard: {text}"); }
        catch (Exception e) { GuardLog.Write("notification failed: " + e.Message); }
    }
}
