using System.Diagnostics;
using LegionModes.Core;
using LegionModes.Core.Supervision;

namespace LegionModes.Keeper;

// Carries out KeeperPolicy: start LLT, restart after a crash, stay down after a graceful exit.
internal sealed class LltSupervisor(KeeperPolicy policy, Action<string> onGiveUp)
{
    public const string ProcessName = "Lenovo Legion Toolkit";
    const string Args = "--minimized --allow-all-power-modes-on-battery";
    static readonly TimeSpan GracefulWindow = TimeSpan.FromSeconds(30);
    static readonly TimeSpan WatchPeriod = TimeSpan.FromSeconds(5);
    static readonly TimeSpan StartRetry = TimeSpan.FromSeconds(60);

    long _gracefulTicks;    // UTC ticks of the last GracefulExit; 0 = none
    volatile bool _updateActive;
    int _generation;        // changes whenever the update flow begins or ends

    public void NoteGracefulExit() => Interlocked.Exchange(ref _gracefulTicks, DateTime.UtcNow.Ticks);

    public void BeginUpdate()
    {
        _updateActive = true;
        Interlocked.Increment(ref _generation);
    }

    public void EndUpdate()
    {
        Interlocked.Exchange(ref _gracefulTicks, 0);
        Interlocked.Increment(ref _generation);
        _updateActive = false;
    }

    public static Process? Find()
    {
        var all = Process.GetProcessesByName(ProcessName);
        for (var i = 1; i < all.Length; i++) all[i].Dispose();
        return all.Length > 0 ? all[0] : null;
    }

    // LLT's manifest asks uiAccess, which only ShellExecute (the AppInfo service) starts; CreateProcess fails with 740 even
    // from an elevated keeper. ShellExecute may hand back no process object: then the running LLT is looked up by name.
    public static Process? Start()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(Paths.LltExe, Args)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(Paths.LltExe)!,
            });
            if (p is not null) return p;
            Thread.Sleep(2000);
            return Find();
        }
        catch (Exception e)
        {
            KeeperLog.Write("cannot start LLT: " + e.Message);
            return null;
        }
    }

    bool GracefulRecently
    {
        get
        {
            var ticks = Interlocked.Read(ref _gracefulTicks);
            return ticks != 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) <= GracefulWindow;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var watching = false;   // after Stay or GiveUp: only look for a process, never start one
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_updateActive) { await Task.Delay(1000, ct); continue; }

                var proc = Find();
                if (proc is null)
                {
                    if (watching) { await Task.Delay(WatchPeriod, ct); continue; }
                    proc = Start();
                    if (proc is null) { await Task.Delay(StartRetry, ct); continue; }   // not judged by the policy; try again later
                    KeeperLog.Write($"started LLT (pid {proc.Id})");
                }
                else if (watching)
                {
                    policy.Reset();
                    watching = false;
                    Interlocked.Exchange(ref _gracefulTicks, 0);
                    KeeperLog.Write($"LLT started by hand (pid {proc.Id}); attached, policy reset");
                }
                else KeeperLog.Write($"attached to LLT (pid {proc.Id})");

                var generation = Volatile.Read(ref _generation);
                using (proc) await proc.WaitForExitAsync(ct);
                if (_updateActive || generation != Volatile.Read(ref _generation))
                {
                    KeeperLog.Write("LLT exit during the update flow; not judged");
                    continue;
                }

                var kind = GracefulRecently ? LltExitKind.Graceful : LltExitKind.Crash;
                Interlocked.Exchange(ref _gracefulTicks, 0);
                var action = policy.OnLltExited(kind);
                KeeperLog.Write($"LLT exited ({kind}) -> {action}");
                switch (action)
                {
                    case KeeperAction.Restart:
                        break;   // the next pass starts it
                    case KeeperAction.Stay:
                        watching = true;
                        break;
                    case KeeperAction.GiveUp:
                        watching = true;
                        onGiveUp("LLT crashed repeatedly; the keeper stopped restarting it.");
                        break;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                KeeperLog.Write("supervisor error: " + e.Message);
                try { await Task.Delay(WatchPeriod, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
