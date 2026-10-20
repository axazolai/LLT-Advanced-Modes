using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using LegionModes.Core.Programs;

namespace LegionModes.Plugin;

internal static class ProcessScanner
{
    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder buffer, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    // Full image path of a pid; null when the process is gone or protected.
    public static string? PathOf(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // Image path and command line of one process from WMI; null when the process is gone (or WMI fails).
    public static (string? Path, string? CommandLine)? InfoOf(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine, ExecutablePath FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o) return (o["ExecutablePath"] as string, o["CommandLine"] as string);
            }
            return null;
        }
        catch (Exception e)
        {
            Log.Error($"read command line of {pid}", e);
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    // Processes that own a visible top-level window with a title (what the taskbar shows), in one pass.
    static HashSet<int> WindowOwners()
    {
        var pids = new HashSet<int>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowTextLengthW(h) > 0
                && GetWindowThreadProcessId(h, out var pid) != 0)
                pids.Add(pid);
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    // One running process for the Programs tab. Windowed: it has a window in the console (owner's) session.
    public sealed record Running(int Pid, string Name, string? Path, string? CommandLine, bool Windowed);

    // Every running process with what can be read of it; Path null when the image is protected.
    public static List<Running> Snapshot()
    {
        var wmi = ReadWmi();
        var windowed = WindowOwners();
        var console = (int)WTSGetActiveConsoleSessionId();
        var list = new List<Running>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id is 0 or 4) continue;                  // Idle and System
                string? path = null, commandLine = null;
                if (wmi is not null && wmi.TryGetValue(p.Id, out var info)) (path, commandLine) = info;
                path ??= PathOf(p.Id);
                list.Add(new Running(p.Id, p.ProcessName, path, commandLine, p.SessionId == console && windowed.Contains(p.Id)));
            }
            catch { /* process exited while reading */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    // pid -> (image path, command line) from one WMI query; null when it fails.
    static Dictionary<int, (string? Path, string? CommandLine)>? ReadWmi()
    {
        try
        {
            var wmi = new Dictionary<int, (string? Path, string? CommandLine)>();
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o) wmi[Convert.ToInt32(o["ProcessId"])] = (o["ExecutablePath"] as string, o["CommandLine"] as string);
            }
            return wmi;
        }
        catch (Exception e)
        {
            Log.Error("process scan through WMI", e);
            return null;
        }
    }

    // Every running process that matches a rule, pid -> class. One WMI query reads path and command line of all processes;
    // when it fails, the processes are listed without command lines (an entry with a command-line pattern then matches nothing).
    public static Dictionary<int, ProgramClass> Scan(ProgramClassifier classifier)
    {
        var result = new Dictionary<int, ProgramClass>();
        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try { names[p.Id] = p.ProcessName; }
            catch { /* process exited while reading */ }
            finally { p.Dispose(); }
        }

        var wmi = ReadWmi();

        foreach (var (pid, name) in names)
        {
            try
            {
                string? path = null, commandLine = null;
                if (wmi is not null && wmi.TryGetValue(pid, out var info)) (path, commandLine) = info;
                path ??= PathOf(pid);
                if (classifier.Classify(path, name, commandLine) is { } c) result[pid] = c;
            }
            catch { /* process exited while reading */ }
        }
        return result;
    }
}
