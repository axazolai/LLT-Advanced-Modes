using LegionModes.Core.Engine;
using LegionModes.Core.Model;

namespace LegionModes.Tests;

public sealed class ProfileEngineTests
{
    static readonly ModesConfig Factory = FactoryProfiles.Create();
    static readonly TimeSpan S1 = TimeSpan.FromSeconds(1);

    const string AcOpenHeavy = "Сеть, открыт, тяжёлая", AcOpenWork = "Сеть, открыт, работа", AcOpenIdle = "Сеть, открыт, простой";

    static Inputs Ac(Workload w, bool ext = false, LidState lid = LidState.Open) => new(PowerSource.Ac, lid, w, ext);
    static Inputs Bat(Workload w, bool ext = false, LidState lid = LidState.Open) => new(PowerSource.Battery, lid, w, ext);
    static Inputs Weak(Workload w) => new(PowerSource.Weak, LidState.Open, w, false);

    static Profile Find(string name, ModesConfig? c = null) => (c ?? Factory).Profiles.Single(p => p.Name == name);

    static ModesConfig Tweak(ModesConfig c, string name, Func<Profile, Profile> change) =>
        c with { Profiles = c.Profiles.Select(p => p.Name == name ? change(p) : p).ToArray() };

    static (ProfileEngine Engine, FakeClock Clock) Make(ModesConfig? c = null, bool autonomy = false)
    {
        var clock = new FakeClock();
        return (new ProfileEngine(c ?? Factory, clock, autonomy), clock);
    }

    [Fact(DisplayName = "@important delay: held 29 s is not in force, 30 s is; Pending and NextChangeAt carry the end")]
    public void Delay()
    {
        var (e, clock) = Make(Tweak(Factory, AcOpenWork, p => p with { DelaySec = 30 }));
        e.Update(Ac(Workload.Idle));
        clock.Advance(S1);
        var d = e.Update(Ac(Workload.Work));
        var start = clock.Now;
        Assert.NotEqual(AcOpenWork, d.Profile.Name);
        Assert.Equal(AcOpenWork, Assert.Single(d.Pending).Profile.Name);
        Assert.Equal(start.AddSeconds(30), d.Pending[0].At);
        Assert.Equal(start.AddSeconds(30), d.NextChangeAt);

        clock.Advance(TimeSpan.FromSeconds(29));
        d = e.Update(Ac(Workload.Work));
        Assert.NotEqual(AcOpenWork, d.Profile.Name);
        Assert.Equal(start.AddSeconds(30), d.NextChangeAt);

        clock.Advance(S1);
        d = e.Update(Ac(Workload.Work));
        Assert.Equal(AcOpenWork, d.Profile.Name);
        Assert.Empty(d.Pending);
    }

    static (ProfileEngine Engine, FakeClock Clock, DateTimeOffset T) HeavyThenWork()
    {
        var (e, clock) = Make();
        e.Update(Ac(Workload.Heavy));
        clock.Advance(TimeSpan.FromMinutes(1));
        return (e, clock, clock.Now);
    }

    [Fact(DisplayName = "@critical hold 60 s: heavy kept until t+60, then work")]
    public void HoldExpires()
    {
        var (e, clock, t) = HeavyThenWork();
        var d = e.Update(Ac(Workload.Work));
        Assert.Equal(AcOpenHeavy, d.Profile.Name);
        Assert.Equal(t.AddSeconds(60), d.NextChangeAt);
        clock.Advance(TimeSpan.FromSeconds(59));
        d = e.Update(Ac(Workload.Work));
        Assert.Equal(AcOpenHeavy, d.Profile.Name);
        Assert.StartsWith("удержание до ", d.Reason);
        clock.Advance(S1);
        d = e.Update(Ac(Workload.Work));
        Assert.Equal(AcOpenWork, d.Profile.Name);
        Assert.Equal("условия выполнены", d.Reason);
    }

    [Fact(DisplayName = "@critical hold: a source change lifts it at once, a monitor change does not")]
    public void HoldLiftedBySourceNotMonitor()
    {
        var (e, clock, _) = HeavyThenWork();
        e.Update(Ac(Workload.Work));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(AcOpenHeavy, e.Update(Ac(Workload.Work, ext: true)).Profile.Name);

        var (e2, clock2, _) = HeavyThenWork();
        e2.Update(Ac(Workload.Work));
        clock2.Advance(TimeSpan.FromSeconds(10));
        e2.Update(Bat(Workload.Work));
        Assert.Equal(AcOpenWork, e2.Update(Ac(Workload.Work)).Profile.Name);
    }

    [Fact(DisplayName = "@critical autonomy: refused off battery, wins on battery, lifted by an adapter")]
    public void AutonomyLifecycle()
    {
        var (e, _) = Make();
        e.Update(Ac(Workload.Idle));
        Assert.Throws<InvalidOperationException>(() => e.Set(OverrideKind.Autonomy));
        e.Update(Bat(Workload.Heavy));
        e.Set(OverrideKind.Autonomy);
        Assert.True(e.Autonomy);
        var d = e.Update(Bat(Workload.Heavy));
        Assert.Equal(ProfileNames.Autonomy, d.Profile.Name);
        Assert.Equal("включена вручную", d.Reason);
        e.Update(Weak(Workload.Heavy));
        Assert.False(e.Autonomy);

        e.Update(Bat(Workload.Heavy));
        e.Set(OverrideKind.Autonomy);
        Assert.NotEqual(ProfileNames.Autonomy, e.Update(Ac(Workload.Heavy)).Profile.Name);
        Assert.False(e.Autonomy);
    }

    [Fact(DisplayName = "@critical autonomy restored from control.json: kept on battery, lifted by the first non-battery Update, also after OnResume")]
    public void AutonomyRestored()
    {
        var (e, _) = Make(autonomy: true);
        Assert.Equal(ProfileNames.Autonomy, e.Update(Bat(Workload.Idle)).Profile.Name);
        Assert.True(e.Autonomy);

        var (e2, _) = Make(autonomy: true);
        Assert.NotEqual(ProfileNames.Autonomy, e2.Update(Ac(Workload.Idle)).Profile.Name);
        Assert.False(e2.Autonomy);

        var (e3, _) = Make(autonomy: true);
        e3.OnResume(Ac(Workload.Idle));
        Assert.NotEqual(ProfileNames.Autonomy, e3.Update(Ac(Workload.Idle)).Profile.Name);
        Assert.False(e3.Autonomy);
    }

    [Fact(DisplayName = "@important source flapping at one instant: holds and overrides gone, empty ParameterDiff")]
    public void SourceFlapping()
    {
        var (e, _, _) = HeavyThenWork();
        var first = e.Update(Ac(Workload.Work));
        Assert.Equal(AcOpenHeavy, first.Profile.Name);   // held
        e.Set(OverrideKind.StayAwake);
        e.Update(Bat(Workload.Work));
        var last = e.Update(Ac(Workload.Work));
        Assert.Equal(AcOpenWork, last.Profile.Name);      // hold gone
        Assert.DoesNotContain(last.Overrides, o => o.Kind == OverrideKind.StayAwake);

        var (e2, _) = Make();
        var a = e2.Update(Ac(Workload.Work));
        e2.Update(Bat(Workload.Work));
        var b = e2.Update(Ac(Workload.Work));
        Assert.Equal(a.Profile.Id, b.Profile.Id);
        Assert.Empty(ParameterDiff.Changed(a.Parameters, b.Parameters, false));
    }

    [Fact(DisplayName = "@important overrides: lifted by a source or lid change as listed; DGpu survives; Pause expires")]
    public void OverridesLifecycle()
    {
        OverrideKind[] lifted = [OverrideKind.ForceWorkload, OverrideKind.StayAwake, OverrideKind.RapidCharge,
            OverrideKind.ChargeFull, OverrideKind.Pause];
        foreach (var change in new Func<Inputs>[] { () => Bat(Workload.Idle), () => Ac(Workload.Idle, lid: LidState.Closed) })
        {
            var (e, _) = Make();
            e.Update(Ac(Workload.Idle));
            foreach (var k in lifted) e.Set(k, k == OverrideKind.ForceWorkload ? Workload.Heavy : null);
            e.Set(OverrideKind.DGpu);
            Assert.Equal(6, e.Update(Ac(Workload.Idle)).Overrides.Count);
            var d = e.Update(change());
            Assert.Equal([OverrideKind.DGpu], d.Overrides.Select(o => o.Kind));
            Assert.True(d.DGpu);
            Assert.False(d.Paused);
        }

        var (p, clock) = Make();
        p.Update(Ac(Workload.Idle));
        p.Set(OverrideKind.Pause);
        var since = clock.Now;
        var pd = p.Update(Ac(Workload.Idle));
        Assert.True(pd.Paused);
        Assert.Equal(since.AddMinutes(60), pd.NextChangeAt);
        Assert.Equal(since.AddMinutes(60), pd.Overrides.Single().Until);
        clock.Advance(TimeSpan.FromMinutes(30));
        p.Set(OverrideKind.Pause);                            // restarts
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.True(p.Update(Ac(Workload.Idle)).Paused);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(p.Update(Ac(Workload.Idle)).Paused);
    }

    [Fact(DisplayName = "@important override effects: force workload, stay awake, rapid vs full charge by the later set")]
    public void OverrideEffects()
    {
        var (e, _) = Make();
        e.Update(Ac(Workload.Idle));
        e.Set(OverrideKind.ForceWorkload, Workload.Heavy);
        Assert.Equal(AcOpenHeavy, e.Update(Ac(Workload.Idle)).Profile.Name);
        e.Clear(OverrideKind.ForceWorkload);

        e.Set(OverrideKind.StayAwake);
        var t = e.Update(Ac(Workload.Idle)).Parameters.Timers.Value;
        Assert.Equal(new IdleTimers(null, 0, 0, 0, null), t);

        e.Set(OverrideKind.RapidCharge);
        e.Set(OverrideKind.ChargeFull);
        Assert.Equal(ChargingMode.Normal, e.Update(Ac(Workload.Idle)).Parameters.Charging.Value);
        e.Set(OverrideKind.RapidCharge);
        Assert.Equal(ChargingMode.Rapid, e.Update(Ac(Workload.Idle)).Parameters.Charging.Value);
    }

    [Fact(DisplayName = "@important stay awake keeps only a static dim stage")]
    public void StayAwakeStaticDim()
    {
        var (e, _) = Make();
        e.Update(Bat(Workload.Idle));
        e.Set(OverrideKind.Autonomy);
        e.Set(OverrideKind.StayAwake);
        var t = e.Update(Bat(Workload.Idle)).Parameters.Timers.Value;
        Assert.Equal(new IdleTimers(new DimStage(0, 30), 0, 0, 0, null), t);
    }

    [Fact(DisplayName = "@important transitions: idle -> heavy, and one transition at the lift of a pause")]
    public void Transitions()
    {
        var (e, _) = Make();
        var d = e.Update(Ac(Workload.Idle));
        Assert.Null(d.Transition!.From);
        Assert.Null(e.Update(Ac(Workload.Idle)).Transition);
        var idle = d.Profile;
        d = e.Update(Ac(Workload.Heavy));
        Assert.Equal(idle.Id, d.Transition!.From!.Id);
        Assert.Equal(AcOpenHeavy, d.Transition.To.Name);

        var (p, _) = Make();
        var di = p.Update(Ac(Workload.Idle));
        p.Set(OverrideKind.Pause);
        var dp = p.Update(Ac(Workload.Heavy));
        Assert.True(dp.Paused);
        Assert.Equal(AcOpenHeavy, dp.Profile.Name);
        Assert.Null(dp.Transition);
        p.Clear(OverrideKind.Pause);
        var lift = p.Update(Ac(Workload.Heavy));
        Assert.Equal(di.Profile.Id, lift.Transition!.From!.Id);
        Assert.Equal(AcOpenHeavy, lift.Transition.To.Name);
        Assert.Null(p.Update(Ac(Workload.Heavy)).Transition);
    }

    [Fact(DisplayName = "@important built-in rules: external monitor keeps Hybrid, lid closed skips internal-only")]
    public void BuiltInRules()
    {
        var (e, _) = Make();
        var d = e.Update(Ac(Workload.Work));
        Assert.Equal(GpuMode.HybridIgpuOnly, d.Parameters.Gpu.Value);
        Assert.Empty(d.Notes);
        d = e.Update(Ac(Workload.Work, ext: true));
        Assert.Equal(GpuMode.Hybrid, d.Parameters.Gpu.Value);
        Assert.Contains(RuleNote.ExternalMonitorKeepsHybrid, d.Notes);

        var cfg = Tweak(Factory, "Закрыт", p => p with
        {
            Set = p.Set with { External = new ExternalMonitor(Projection.InternalOnly, new ExternalRate(ExternalRateKind.Maximum)) }
        });
        var (c, _) = Make(cfg);
        d = c.Update(Ac(Workload.Idle, lid: LidState.Closed));
        Assert.Equal(Projection.DoNotTouch, d.Parameters.External.Value.Projection);
        Assert.Contains(RuleNote.LidClosedSkipsInternalOnly, d.Notes);
        Assert.Equal(Projection.Extend, Make(cfg).Engine.Update(Ac(Workload.Idle)).Parameters.External.Value.Projection);
    }

    [Fact(DisplayName = "@critical forced sleep: lid closed without a display sleeps after the minutes; display or open lid cancels; resume restarts")]
    public void ForcedSleep()
    {
        var (e, clock) = Make();
        e.Update(Bat(Workload.Idle));
        clock.Advance(TimeSpan.FromSeconds(5));
        var t = clock.Now;
        var d = e.Update(Bat(Workload.Idle, lid: LidState.Closed));
        Assert.Equal(t.AddMinutes(1), d.ForcedSleepAt);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(t.AddMinutes(1), e.Update(Bat(Workload.Idle, lid: LidState.Closed)).ForcedSleepAt);
        Assert.Null(e.Update(Bat(Workload.Idle, ext: true, lid: LidState.Closed)).ForcedSleepAt);

        e.OnResume(Bat(Workload.Idle));
        Assert.Null(e.Update(Bat(Workload.Idle)).ForcedSleepAt);

        var (c, cc) = Make();
        c.Update(Bat(Workload.Idle, lid: LidState.Closed));
        cc.Advance(TimeSpan.FromMinutes(50));
        c.OnResume(Bat(Workload.Idle, lid: LidState.Closed));
        var resume = cc.Now;
        Assert.Equal(resume.AddMinutes(1), c.Update(Bat(Workload.Idle, lid: LidState.Closed)).ForcedSleepAt);

        var (s, _) = Make();
        Assert.NotNull(s.Update(Bat(Workload.Idle, lid: LidState.Closed)).ForcedSleepAt);
        s.Set(OverrideKind.StayAwake);
        Assert.Null(s.Update(Bat(Workload.Idle, lid: LidState.Closed)).ForcedSleepAt);
    }

    [Fact(DisplayName = "@important ParameterDiff: only the parameters that differ; full gives all nine")]
    public void Diff()
    {
        var (e, _) = Make();
        var work = e.Update(Ac(Workload.Work)).Parameters;
        var (e2, _) = Make();
        var idle2 = e2.Update(Ac(Workload.Idle)).Parameters;
        Assert.Equal([ParameterId.Power, ParameterId.Lighting], ParameterDiff.Changed(work, idle2, false));

        var (b, _) = Make();
        var open = b.Update(Bat(Workload.Work)).Parameters;
        var (c, _) = Make();
        var closed = c.Update(Bat(Workload.Work, lid: LidState.Closed)).Parameters;
        Assert.Equal(
            [ParameterId.Panel, ParameterId.External, ParameterId.Timers, ParameterId.Lighting],
            ParameterDiff.Changed(open, closed, false));
        Assert.Equal(9, ParameterDiff.Changed(open, closed, true).Count);
        Assert.Equal(9, ParameterDiff.Changed(null, closed, false).Count);
    }

    [Fact(DisplayName = "@important IdleLightingPolicy: switch off once, restore on On, nothing while inactive")]
    public void IdleLighting()
    {
        const DisplayStateKind On = DisplayStateKind.On, Dim = DisplayStateKind.Dimmed, Off = DisplayStateKind.Off;
        Assert.Equal(LightingAction.SwitchOff, IdleLightingPolicy.Next(On, Dim, true, false));
        Assert.Equal(LightingAction.None, IdleLightingPolicy.Next(Dim, Off, true, true));
        Assert.Equal(LightingAction.Restore, IdleLightingPolicy.Next(Off, On, true, true));
        Assert.Equal(LightingAction.None, IdleLightingPolicy.Next(Off, On, true, false));
        Assert.Equal(LightingAction.None, IdleLightingPolicy.Next(On, Dim, false, false));
        Assert.Equal(LightingAction.None, IdleLightingPolicy.Next(Dim, On, true, false));
    }

    [Fact(DisplayName = "@important WindowsTimers: seconds, hibernate after sleep, static brightness")]
    public void Timers()
    {
        Assert.Equal(new WindowsTimerValues(300, 20, 600, 1800, 5400, null),
            WindowsTimers.From(new IdleTimers(new DimStage(5, 20), 10, 30, 60, null)));
        Assert.Equal(3600u, WindowsTimers.From(new IdleTimers(null, 10, 0, 60, null)).HibernateIdleSec);
        var s = WindowsTimers.From(new IdleTimers(new DimStage(0, 30), 3, 10, 60, null));
        Assert.Equal(0u, s.DimSec);
        Assert.Equal(30, s.StaticBrightness);
        Assert.Equal(50u, WindowsTimers.From(new IdleTimers(null, 0, 0, 0, null)).DimmedBrightness);
    }

    [Fact(DisplayName = "@important coalescer yields one take after the window from the first signal, full if any was full")]
    public void Coalescer()
    {
        var clock = new FakeClock();
        var c = new EventCoalescer(clock, TimeSpan.FromSeconds(2));
        Assert.False(c.TryTake(out _));
        c.Signal(false);
        clock.Advance(TimeSpan.FromSeconds(0.7));
        c.Signal(true);
        clock.Advance(TimeSpan.FromSeconds(0.8));
        c.Signal(false);
        clock.Advance(TimeSpan.FromSeconds(0.4));
        Assert.False(c.TryTake(out _));                 // 1.9 s since the first
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(c.TryTake(out var full));
        Assert.True(full);
        Assert.False(c.TryTake(out _));
    }
}
