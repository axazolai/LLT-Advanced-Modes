using LegionModes.Core.Ipc;
using LegionModes.Core.Supervision;
using Xunit;

namespace LegionModes.Tests;

public class SupervisionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
    private static TimeSpan M(double m) => TimeSpan.FromMinutes(m);

    [Fact(DisplayName = "@important crash, crash restart; third crash within 10 min gives up; third at 11 min restarts")]
    public void CrashWindow()
    {
        var clock = new FakeClock();
        var p = new KeeperPolicy(clock);
        Assert.Equal(KeeperAction.Restart, p.OnLltExited(LltExitKind.Crash));
        clock.Advance(M(1));
        Assert.Equal(KeeperAction.Restart, p.OnLltExited(LltExitKind.Crash));
        clock.Advance(M(1));
        Assert.Equal(KeeperAction.GiveUp, p.OnLltExited(LltExitKind.Crash));

        var c2 = new FakeClock();
        var p2 = new KeeperPolicy(c2);
        p2.OnLltExited(LltExitKind.Crash);
        c2.Advance(M(5));
        p2.OnLltExited(LltExitKind.Crash);
        c2.Advance(M(6));
        Assert.Equal(KeeperAction.Restart, p2.OnLltExited(LltExitKind.Crash));
    }

    [Fact(DisplayName = "@important after GiveUp crashes keep giving up until Reset; graceful stays")]
    public void GiveUpSticks()
    {
        var clock = new FakeClock();
        var p = new KeeperPolicy(clock, maxCrashes: 2);
        Assert.Equal(KeeperAction.Restart, p.OnLltExited(LltExitKind.Crash));
        Assert.Equal(KeeperAction.GiveUp, p.OnLltExited(LltExitKind.Crash));
        clock.Advance(M(30));
        Assert.Equal(KeeperAction.GiveUp, p.OnLltExited(LltExitKind.Crash));
        Assert.Equal(KeeperAction.Stay, p.OnLltExited(LltExitKind.Graceful));
        p.Reset();
        Assert.Equal(KeeperAction.Restart, p.OnLltExited(LltExitKind.Crash));
    }

    [Fact(DisplayName = "@important graceful exit stays")]
    public void GracefulStays() =>
        Assert.Equal(KeeperAction.Stay, new KeeperPolicy(new FakeClock()).OnLltExited(LltExitKind.Graceful));

    private static GuardState KeeperState(bool active = true, DateTimeOffset? taskRun = null, DateTimeOffset? direct = null) =>
        new(active, false, T0, taskRun, false, null, null, null, null, null, direct);

    [Fact(DisplayName = "@important keeper missing: task first, direct start after 30 s, nothing without a session")]
    public void KeeperCycle()
    {
        var now = T0 + M(5);
        Assert.Equal(new[] { GuardAction.RunKeeperTask }, GuardPolicy.Decide(KeeperState(), now));
        Assert.Empty(GuardPolicy.Decide(KeeperState(taskRun: now - S(29)), now));
        Assert.Equal(new[] { GuardAction.StartKeeperDirect }, GuardPolicy.Decide(KeeperState(taskRun: now - S(30)), now));
        Assert.Empty(GuardPolicy.Decide(KeeperState(active: false), now));
        Assert.Empty(GuardPolicy.Decide(KeeperState(active: false, taskRun: now - S(40)), now));
        var running = KeeperState() with { KeeperRunning = true };
        Assert.Empty(GuardPolicy.Decide(running, now));
    }

    [Fact(DisplayName = "@important after a direct start the task runs again at 60 s")]
    public void DirectStartRepeats()
    {
        var now = T0 + M(5);
        var taskRun = now - S(100);
        Assert.Empty(GuardPolicy.Decide(KeeperState(taskRun: taskRun, direct: now - S(59)), now));
        Assert.Equal(new[] { GuardAction.RunKeeperTask }, GuardPolicy.Decide(KeeperState(taskRun: taskRun, direct: now - S(60)), now));
        Assert.Equal(new[] { GuardAction.StartKeeperDirect },
            GuardPolicy.Decide(KeeperState(taskRun: now - S(30), direct: now - S(90)), now));
    }

    private static Heartbeat Hb(DateTimeOffset at, bool ext = false) => new(at, false, ext, true);

    private static GuardState Lid(DateTimeOffset closedAt, Heartbeat? hb = null, DateTimeOffset? update = null,
        DateTimeOffset? resumed = null, DateTimeOffset? slept = null, bool keeperUp = true) =>
        new(true, keeperUp, null, null, true, closedAt, hb, update, resumed, slept);

    [Fact(DisplayName = "@critical lid closed with a silent plugin sleeps; fresh heartbeat does not; stale display heartbeat does")]
    public void LidSilence()
    {
        var closed = T0;
        var now = closed + S(61);
        Assert.Equal(new[] { GuardAction.SleepNow }, GuardPolicy.Decide(Lid(closed, Hb(now - S(61))), now));
        Assert.Empty(GuardPolicy.Decide(Lid(closed, Hb(now - S(10))), now));
        Assert.Equal(new[] { GuardAction.SleepNow }, GuardPolicy.Decide(Lid(closed, Hb(now - S(61), ext: true)), now));

        var n2 = closed + M(15);
        var upd = n2 - M(1);
        Assert.Empty(GuardPolicy.Decide(Lid(closed, Hb(n2 - M(5)), upd), n2));
        Assert.Equal(new[] { GuardAction.SleepNow }, GuardPolicy.Decide(Lid(closed, Hb(n2 - M(10)), upd), n2));
        Assert.Equal(new[] { GuardAction.SleepNow }, GuardPolicy.Decide(Lid(closed, Hb(now - S(61)), now - M(31)), now));
    }

    [Fact(DisplayName = "@critical lid closed and no heartbeat ever sleeps after 60 s")]
    public void LidNoHeartbeat()
    {
        Assert.Empty(GuardPolicy.Decide(Lid(T0), T0 + S(59)));
        Assert.Equal(new[] { GuardAction.SleepNow }, GuardPolicy.Decide(Lid(T0), T0 + S(60)));
        Assert.Empty(GuardPolicy.Decide(Lid(T0) with { LidClosed = false }, T0 + S(120)));
    }

    [Fact(DisplayName = "@critical safety sleep happens once per lid closing; a resume restarts the count")]
    public void LidOnceAndResume()
    {
        var sl = T0 + S(61);
        Assert.Empty(GuardPolicy.Decide(Lid(T0, slept: sl), sl));
        Assert.Empty(GuardPolicy.Decide(Lid(T0, slept: sl), sl + S(59)));
        Assert.Equal(new[] { GuardAction.SleepNow }, GuardPolicy.Decide(Lid(T0, slept: sl), sl + S(60)));

        var resumed = T0 + M(30);
        var slept = T0 + S(61);
        Assert.Empty(GuardPolicy.Decide(Lid(T0, resumed: resumed, slept: slept), resumed + S(59)));
        Assert.Equal(new[] { GuardAction.SleepNow }, GuardPolicy.Decide(Lid(T0, resumed: resumed, slept: slept), resumed + S(60)));
    }

    [Fact(DisplayName = "@important a heartbeat from the future counts as now")]
    public void FutureHeartbeatClamped()
    {
        var now = T0 + M(10);
        var s = Lid(now - S(61), Hb(now + M(5)));
        Assert.Empty(GuardPolicy.Decide(s, now));
        // the guard keeps the clamped time (receipt time) as LastHeartbeat.At
        var stored = s with { LastHeartbeat = Hb(now) };
        Assert.Equal(new[] { GuardAction.SleepNow }, GuardPolicy.Decide(stored, now + S(60)));
        Assert.Empty(GuardPolicy.Decide(stored, now + S(59)));
    }

    [Fact(DisplayName = "@important after a resume the keeper task runs first even if a task ran before")]
    public void ResumeRestartsKeeperCycle()
    {
        var now = T0 + M(10);
        var s = KeeperState(taskRun: now - S(100)) with { ResumedAt = now - S(50) };
        Assert.Equal(new[] { GuardAction.RunKeeperTask }, GuardPolicy.Decide(s, now));
    }

    [Fact(DisplayName = "@critical lid safety does not depend on the session and comes after the keeper action")]
    public void LidIndependentAndOrdered()
    {
        var s = Lid(T0, keeperUp: false) with { UserSessionActive = false };
        Assert.Equal(new[] { GuardAction.SleepNow }, GuardPolicy.Decide(s, T0 + S(61)));
        var s2 = Lid(T0, keeperUp: false);
        Assert.Equal(new[] { GuardAction.RunKeeperTask, GuardAction.SleepNow }, GuardPolicy.Decide(s2, T0 + S(61)));
    }
}
