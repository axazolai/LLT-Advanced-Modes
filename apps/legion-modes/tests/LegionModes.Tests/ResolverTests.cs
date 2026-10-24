using LegionModes.Core.Engine;
using LegionModes.Core.Model;

namespace LegionModes.Tests;

public sealed class ResolverTests
{
    static readonly ModesConfig Factory = FactoryProfiles.Create();

    static Profile Find(string name) => Factory.Profiles.Single(p => p.Name == name);

    static string Dim(DimStage? d) => d is null ? "-" : $"{d.Minutes}>{d.Brightness}";

    static string Row(EffectiveParameters e) => string.Join("|",
        e.Power.Source.Name,
        e.Gpu.Value,
        e.Panel.Value.Kind == PanelRateKind.Fixed ? "Fixed" + e.Panel.Value.Hz : e.Panel.Value.Kind.ToString(),
        $"{e.External.Value.Projection}/{(e.External.Value.Rate.Kind == ExternalRateKind.Maximum ? "Max" : e.External.Value.Rate.Hz.ToString())}",
        e.Charging.Value,
        e.Usb.Value,
        $"{e.Processor.Value.Boost}/{e.Processor.Value.Epp}/{e.Processor.Value.PowerMode}",
        $"{Dim(e.Timers.Value.Dim)}/{e.Timers.Value.DisplayOffMin}/{e.Timers.Value.SleepMin}/{e.Timers.Value.HibernateMin}/{e.Timers.Value.ForcedSleepMin?.ToString() ?? "-"}",
        $"{e.Lighting.Value.Profile}/{e.Lighting.Value.Brightness}");

    // Expected values, written from refs/mode-defaults.md sections 2-5 (not from the resolver's output).
    // Columns: selected profile ; power from ; GPU ; panel ; external ; charging ; USB ; processor ; timers ; lighting.
    const string RootProc = "Enabled/33/Balanced", Proc50 = "Enabled/50/Balanced", Proc80 = "Enabled/80/BestPowerEfficiency",
        Saver = "Disabled/100/BestPowerEfficiency";
    const string TRoot = "5>20/10/30/0/-", TClosed = "-/10/30/0/1", TWeak = "3>20/5/20/180/-", TBat = "3>15/5/15/60/-",
        TAiOpen = "5>20/10/0/0/-", THeavyOpen = "10>20/15/60/0/-", TAcClosedHeavy = "-/15/60/0/1", TWeakClosed = "-/5/20/180/1",
        TBatClosed = "-/5/15/30/1", TBatOpenIdle = "1>15/3/10/60/-", TBatClosedIdle = "-/3/10/30/1";
    const string Igpu = "HybridIgpuOnly", Hyb = "Hybrid";

    static string Exp(string selected, string row) => selected + " ;; " + row;

    static IEnumerable<(PowerSource S, LidState L, Workload W, string Expected)> Table()
    {
        yield return (PowerSource.Ac, LidState.Open, Workload.Idle, Exp("Сеть, открыт, простой",
            $"Сеть, открыт, простой|{Hyb}|Drr|Extend/Max|Conservation|Always|{RootProc}|{TRoot}|3/3"));
        yield return (PowerSource.Ac, LidState.Open, Workload.Work, Exp("Сеть, открыт, работа",
            $"Всегда|{Hyb}|Drr|Extend/Max|Conservation|Always|{RootProc}|{TRoot}|1/3"));
        yield return (PowerSource.Ac, LidState.Open, Workload.Ai, Exp("Сеть, открыт, ИИ",
            $"Сеть, открыт, ИИ|{Hyb}|Drr|Extend/Max|Conservation|Always|{RootProc}|{TAiOpen}|3/3"));
        yield return (PowerSource.Ac, LidState.Open, Workload.Heavy, Exp("Сеть, открыт, тяжёлая",
            $"Сеть, открыт, тяжёлая|{Hyb}|Fixed240|Extend/Max|Conservation|Always|{RootProc}|{THeavyOpen}|2/3"));

        yield return (PowerSource.Ac, LidState.Closed, Workload.Idle, Exp("Сеть, закрыт, простой",
            $"Сеть, закрыт, простой|{Hyb}|DoNotTouch|ExternalOnly/Max|Conservation|Always|{RootProc}|{TClosed}|3/0"));
        yield return (PowerSource.Ac, LidState.Closed, Workload.Work, Exp("Сеть, закрыт, работа",
            $"Сеть, закрыт|{Hyb}|DoNotTouch|ExternalOnly/Max|Conservation|Always|{RootProc}|{TClosed}|3/0"));
        yield return (PowerSource.Ac, LidState.Closed, Workload.Ai, Exp("Сеть, закрыт",
            $"Сеть, закрыт|{Hyb}|DoNotTouch|ExternalOnly/Max|Conservation|Always|{RootProc}|{TClosed}|3/0"));
        yield return (PowerSource.Ac, LidState.Closed, Workload.Heavy, Exp("Сеть, закрыт, тяжёлая",
            $"Сеть, закрыт, тяжёлая|{Hyb}|DoNotTouch|ExternalOnly/Max|Conservation|Always|{RootProc}|{TAcClosedHeavy}|3/0"));

        yield return (PowerSource.Weak, LidState.Open, Workload.Idle, Exp("Слабый адаптер, открыт, простой",
            $"Слабый адаптер, открыт, простой|{Igpu}|Fixed60|Extend/60|Normal|WhenSleeping|{Saver}|{TWeak}|4/1"));
        yield return (PowerSource.Weak, LidState.Open, Workload.Work, Exp("Слабый адаптер, открыт, работа",
            $"Слабый адаптер|{Igpu}|Fixed60|Extend/60|Normal|WhenSleeping|{Proc50}|{TWeak}|4/1"));
        yield return (PowerSource.Weak, LidState.Open, Workload.Ai, Exp("Слабый адаптер",
            $"Слабый адаптер|{Igpu}|Fixed60|Extend/60|Normal|WhenSleeping|{Proc50}|{TWeak}|4/1"));
        yield return (PowerSource.Weak, LidState.Open, Workload.Heavy, Exp("Слабый адаптер, открыт, тяжёлая",
            $"Слабый адаптер, открыт, тяжёлая|{Hyb}|Fixed120|Extend/60|Normal|WhenSleeping|{Proc50}|{TWeak}|4/1"));

        // lid closed on a weak adapter: no workload profile exists, «Слабый адаптер, закрыт» is in force for all four;
        // the panel comes from «Закрыт» (earlier in the list than «Слабый адаптер»)
        foreach (var w in new[] { Workload.Idle, Workload.Work, Workload.Ai, Workload.Heavy })
            yield return (PowerSource.Weak, LidState.Closed, w, Exp("Слабый адаптер, закрыт",
                $"Слабый адаптер|{Igpu}|DoNotTouch|ExternalOnly/60|Normal|WhenSleeping|{Proc50}|{TWeakClosed}|3/0"));

        yield return (PowerSource.Battery, LidState.Open, Workload.Idle, Exp("Батарея, открыт, простой",
            $"Батарея, открыт, простой|{Igpu}|Fixed60|Extend/60|DoNotTouch|Off|{Saver}|{TBatOpenIdle}|4/0"));
        yield return (PowerSource.Battery, LidState.Open, Workload.Work, Exp("Батарея, открыт, работа",
            $"Батарея|{Igpu}|Fixed60|Extend/60|DoNotTouch|Off|{Proc80}|{TBat}|4/1"));
        yield return (PowerSource.Battery, LidState.Open, Workload.Ai, Exp("Батарея",
            $"Батарея|{Igpu}|Fixed60|Extend/60|DoNotTouch|Off|{Proc80}|{TBat}|4/1"));
        yield return (PowerSource.Battery, LidState.Open, Workload.Heavy, Exp("Батарея, открыт, тяжёлая",
            $"Батарея, открыт, тяжёлая|{Hyb}|Fixed60|Extend/60|DoNotTouch|Off|{Proc50}|{TBat}|4/1"));

        yield return (PowerSource.Battery, LidState.Closed, Workload.Idle, Exp("Батарея, закрыт, простой",
            $"Батарея, закрыт, простой|{Igpu}|DoNotTouch|ExternalOnly/60|DoNotTouch|Off|{Saver}|{TBatClosedIdle}|3/0"));
        yield return (PowerSource.Battery, LidState.Closed, Workload.Work, Exp("Батарея, закрыт, работа",
            $"Батарея|{Igpu}|DoNotTouch|ExternalOnly/60|DoNotTouch|Off|{Proc80}|{TBatClosed}|3/0"));
        yield return (PowerSource.Battery, LidState.Closed, Workload.Ai, Exp("Батарея, закрыт",
            $"Батарея|{Igpu}|DoNotTouch|ExternalOnly/60|DoNotTouch|Off|{Proc80}|{TBatClosed}|3/0"));
        yield return (PowerSource.Battery, LidState.Closed, Workload.Heavy, Exp("Батарея, закрыт, тяжёлая",
            $"Батарея, закрыт, тяжёлая|{Hyb}|DoNotTouch|ExternalOnly/60|DoNotTouch|Off|{Proc50}|{TBatClosed}|3/0"));
    }

    [Fact(DisplayName = "@critical factory set: the effective parameters of all 24 states equal mode-defaults.md")]
    public void FactoryStatesMatchReference()
    {
        var rows = Table().ToList();
        Assert.Equal(24, rows.Count);
        Assert.Equal(24, rows.Select(r => (r.S, r.L, r.W)).Distinct().Count());
        foreach (var (s, l, w, expected) in rows)
        {
            var state = new AxisState(s, l, w, MonitorState.None);
            var chosen = Resolver.Select(Factory, Factory.Profiles.Where(p => p.Kind == ProfileKind.Automatic && p.Conditions.Matches(state)));
            var actual = Exp(chosen.Name, Row(Resolver.Effective(Factory, chosen)));
            Assert.True(expected == actual, $"{s}/{l}/{w}:\n  expected {expected}\n  actual   {actual}");
        }
    }

    [Fact(DisplayName = "@important a parameter set by two ancestors of equal specificity comes from the earlier one, and Source says so")]
    public void EqualSpecificityAncestorsTakeListOrder()
    {
        var e = Resolver.Effective(Factory, Find("Батарея, закрыт, простой"));
        Assert.Equal(PanelRateKind.DoNotTouch, e.Panel.Value.Kind);
        Assert.Equal("Закрыт", e.Panel.Source.Name);
        Assert.Equal("Батарея", e.Usb.Source.Name);
        Assert.Equal("Батарея, закрыт, простой", e.Processor.Source.Name);
        Assert.Equal("Всегда", e.Gpu.Source.Name);
        var names = Resolver.Ancestors(Factory, Find("Батарея, закрыт, простой")).Select(p => p.Name);
        Assert.Equal(["Батарея, закрыт", "Закрыт", "Батарея", "Всегда"], names);
    }

    [Fact(DisplayName = "@important Select: highest specificity, then list order, empty gives the root")]
    public void SelectBySpecificityThenOrder()
    {
        Assert.Equal("Батарея, закрыт",
            Resolver.Select(Factory, [Find("Батарея"), Find("Закрыт"), Find("Батарея, закрыт")]).Name);
        Assert.Equal("Сеть, открыт, тяжёлая",
            Resolver.Select(Factory, [Find("Сеть, открыт, работа"), Find("Сеть, открыт, тяжёлая")]).Name);
        Assert.Equal("Сеть, открыт, тяжёлая",
            Resolver.Select(Factory, [Find("Сеть, открыт, тяжёлая"), Find("Сеть, открыт, работа")]).Name);
        Assert.Same(Factory.Root, Resolver.Select(Factory, []));
    }

    static Pipeline Pipe(string key) => new(Guid.NewGuid(), key, "{}");

    static Profile Auto(string name, Conditions c, Pipeline[] pipes, PipelineSlot[]? order = null) =>
        new(Guid.NewGuid(), name, ProfileKind.Automatic, c, 0, 0, new Parameters(), pipes, order ?? []);

    static ModesConfig Cfg(Profile root, params Profile[] rest) =>
        FactoryProfiles.Create() with { Profiles = [root, .. rest] };

    static Profile RootWith(params Pipeline[] pipes)
    {
        var r = FactoryProfiles.Create().Root;
        return r with { Pipelines = pipes, Order = [] };
    }

    static Profile Manual(params Pipeline[] pipes) =>
        new(Guid.NewGuid(), ProfileNames.Autonomy, ProfileKind.Manual, new Conditions(), 0, 0, new Parameters(), pipes, []);

    static readonly Conditions BatC = new(Source: PowerSource.Battery);

    [Fact(DisplayName = "@important pipelines: every enabled pipeline on one event runs, own and inherited, in list order")]
    public void SameEventPipelinesAllRun()
    {
        Pipeline inherited1 = Pipe("enter"), inherited2 = Pipe("enter"), own1 = Pipe("enter"), own2 = Pipe("enter");
        var parent = Auto("Parent", BatC, [inherited1, inherited2]);
        var child = Auto("Child", new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed), [own1, own2]);
        var list = Resolver.Pipelines(Cfg(RootWith(), parent, child, Manual()), child);
        Assert.Equal([own1, own2, inherited1, inherited2], list.Select(x => x.Pipeline));
        Assert.All(list, x => Assert.Equal(PipelineState.Wins, x.State));
    }

    [Fact(DisplayName = "@important pipelines: a slot switches a pipeline off here and moves an inherited one above an own one")]
    public void SlotsSwitchOffAndOrder()
    {
        Pipeline inherited = Pipe("enter"), own = Pipe("enter");
        var parent = Auto("Parent", BatC, [inherited]);
        var child = Auto("Child", new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed), [own],
            [new PipelineSlot(inherited.Id, true), new PipelineSlot(own.Id, false)]);
        var list = Resolver.Pipelines(Cfg(RootWith(), parent, child, Manual()), child);
        Assert.Equal([inherited, own], list.Select(x => x.Pipeline));
        Assert.Equal([PipelineState.Wins, PipelineState.Off], list.Select(x => x.State));
        Assert.Same(parent, list[0].Source);
    }

    [Fact(DisplayName = "@critical pipelines: one switched off in 2 does not reach 3 (1 -> 2 -> 3); it is listed last, blocked by 2")]
    public void SwitchedOffInMiddleBlocksTheChain()
    {
        Pipeline r = Pipe("enter"), other = Pipe("exit");
        var root = RootWith(r, other);
        var bat = Auto("Bat", BatC, [], [new PipelineSlot(r.Id, false)]);
        var batClosed = Auto("BatClosed", new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed), []);
        var c = Cfg(root, bat, batClosed, Manual());

        var inBat = Resolver.Pipelines(c, bat);
        Assert.Equal(PipelineState.Off, inBat.Single(x => x.Pipeline == r).State);   // switched off where it is shown

        var list = Resolver.Pipelines(c, batClosed);
        Assert.Equal([other, r], list.Select(x => x.Pipeline));
        Assert.Equal(PipelineState.Wins, list[0].State);
        Assert.Equal(PipelineState.Blocked, list[1].State);
        Assert.Same(bat, list[1].BlockedBy);
        Assert.Same(root, list[1].Source);

        // A slot of the blocked one in 3 neither runs it nor moves it.
        var withSlot = batClosed with { Order = [new PipelineSlot(r.Id, true)] };
        var again = Resolver.Pipelines(Cfg(root, bat, withSlot, Manual()), withSlot);
        Assert.Equal(PipelineState.Blocked, again.Single(x => x.Pipeline == r).State);
    }

    [Fact(DisplayName = "@critical pipelines: with two parents one open chain is enough; switched off in both it is blocked")]
    public void OneOpenParentChainIsEnough()
    {
        Pipeline r = Pipe("enter");
        var bat = Auto("Bat", BatC, [], [new PipelineSlot(r.Id, false)]);
        var closed = Auto("Closed", new Conditions(Lid: LidState.Closed), []);
        var both = Auto("Both", new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed), []);
        var list = Resolver.Pipelines(Cfg(RootWith(r), bat, closed, both, Manual()), both);
        Assert.Equal(PipelineState.Wins, list.Single(x => x.Pipeline == r).State);
        Assert.Equivalent(new[] { bat.Id, closed.Id }, Resolver.Parents(Cfg(RootWith(r), bat, closed, both, Manual()), both).Select(x => x.Id), strict: true);

        var closedOff = closed with { Order = [new PipelineSlot(r.Id, false)] };
        var blocked = Resolver.Pipelines(Cfg(RootWith(r), bat, closedOff, both, Manual()), both).Single(x => x.Pipeline == r);
        Assert.Equal(PipelineState.Blocked, blocked.State);
    }

    [Fact(DisplayName = "@important pipelines: one switched off by its own profile reaches none of its descendants")]
    public void SwitchedOffByOwnerBlocksDescendants()
    {
        Pipeline b = Pipe("exit");
        var bat = Auto("Bat", BatC, [b], [new PipelineSlot(b.Id, false)]);
        var batClosed = Auto("BatClosed", new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed), []);
        var item = Resolver.Pipelines(Cfg(RootWith(), bat, batClosed, Manual()), batClosed).Single();
        Assert.Equal(PipelineState.Blocked, item.State);
        Assert.Same(bat, item.BlockedBy);
    }

    [Fact(DisplayName = "@critical pipelines: saving a profile keeps its own «off» for a pipeline blocked from above, so it stays off when unblocked")]
    public void SaveKeepsSlotsOfBlockedPipelines()
    {
        Pipeline r = Pipe("enter"), own = Pipe("exit");
        var bat = Auto("Bat", BatC, [], [new PipelineSlot(r.Id, false)]);
        var child = Auto("BatClosed", new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed), [own], [new PipelineSlot(r.Id, false)]);
        var c = Cfg(RootWith(r), bat, child, Manual());
        Assert.Equal(PipelineState.Blocked, Resolver.Pipelines(c, child).Single(x => x.Pipeline == r).State);

        // The automation window shows only the own pipeline as editable; the save keeps the child's «off» for r.
        var saved = child with { Order = Resolver.KeepBlockedSlots(c, child, [new PipelineSlot(own.Id, true)]) };
        Assert.Contains(new PipelineSlot(r.Id, false), saved.Order);

        var batOn = bat with { Order = [] };
        Assert.Equal(PipelineState.Off, Resolver.Pipelines(Cfg(RootWith(r), batOn, saved, Manual()), saved).Single(x => x.Pipeline == r).State);
    }

    [Fact(DisplayName = "@important pipelines: a parent that does not carry the pipeline is no open chain; BlockedBy names where it can be switched on")]
    public void UnrelatedParentIsNoOpenChain()
    {
        // «BatClosed» has two parents, «Bat» and «Closed»; b belongs to «Bat», which switched it off. «Closed» never had b.
        Pipeline b = Pipe("exit");
        var bat = Auto("Bat", BatC, [b], [new PipelineSlot(b.Id, false)]);
        var closed = Auto("Closed", new Conditions(Lid: LidState.Closed), []);
        var batClosed = Auto("BatClosed", new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed), []);
        var c = Cfg(RootWith(), bat, closed, batClosed, Manual());
        Assert.Equal(2, Resolver.Parents(c, batClosed).Count);
        var item = Resolver.Pipelines(c, batClosed).Single();
        Assert.Equal(PipelineState.Blocked, item.State);
        Assert.Same(bat, item.BlockedBy);

        // b on in «Bat», off in «BatClosed», and off in «Bat» again: under «BatClosed» the place to switch it on is «Bat».
        var deep = Auto("Deep", new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed, Load: Workload.Idle), []);
        var batClosedOff = batClosed with { Order = [new PipelineSlot(b.Id, false)] };
        Assert.Same(batClosedOff, Resolver.Pipelines(Cfg(RootWith(), bat with { Order = [] }, closed, batClosedOff, deep, Manual()), deep).Single().BlockedBy);
        Assert.Same(bat, Resolver.Pipelines(Cfg(RootWith(), bat, closed, batClosedOff, deep, Manual()), deep).Single().BlockedBy);
    }

    [Fact(DisplayName = "@important pipelines: «Автономия» gets the root's pipeline unless the root switched it off")]
    public void AutonomyFollowsTheRootSwitch()
    {
        Pipeline r = Pipe("enter");
        var manual = Manual();
        Assert.Equal(PipelineState.Wins, Resolver.Pipelines(Cfg(RootWith(r), manual), manual).Single().State);
        var rootOff = RootWith(r) with { Order = [new PipelineSlot(r.Id, false)] };
        Assert.Equal(PipelineState.Blocked, Resolver.Pipelines(Cfg(rootOff, manual), manual).Single().State);
    }

    [Fact(DisplayName = "@important pipelines: a manual profile inherits only the root's; inherited order is specificity then list")]
    public void ManualInheritsRootOnly()
    {
        Pipeline r = Pipe("enter"), b = Pipe("exit"), z = Pipe("exit");
        var bat = Auto("Bat", BatC, [b]);
        var closed = Auto("Closed", new Conditions(Lid: LidState.Closed), [z]);
        var manual = Manual();
        var c = Cfg(RootWith(r), closed, bat, manual);
        Assert.Equal([r], Resolver.Pipelines(c, manual).Select(x => x.Pipeline));
        var both = Auto("Both", new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed), []);
        var c2 = Cfg(RootWith(r), closed, bat, both, manual);
        var list = Resolver.Pipelines(c2, both);
        Assert.Equal([z, b, r], list.Select(x => x.Pipeline));
        Assert.All(list, x => Assert.Equal(PipelineState.Wins, x.State));
    }

    [Fact(DisplayName = "@important pipelines: a slot naming a deleted pipeline is ignored and a repeated slot counts once")]
    public void DanglingSlotIgnored()
    {
        Pipeline a = Pipe("enter"), b = Pipe("enter");
        var p = Auto("A", BatC, [a, b], [new PipelineSlot(Guid.NewGuid(), true), new PipelineSlot(b.Id, true), new PipelineSlot(b.Id, false)]);
        var list = Resolver.Pipelines(Cfg(RootWith(), p, Manual()), p);
        Assert.Equal([b, a], list.Select(x => x.Pipeline));
        Assert.All(list, x => Assert.Equal(PipelineState.Wins, x.State));      // the first slot of b (enabled) counts
    }

    [Fact(DisplayName = "@important overlaps: «Батарея» lists «Закрыт» won by «Закрыт»; an incompatible source is not listed")]
    public void OverlapsListCompatibleEqualSpecificity()
    {
        var o = Resolver.Overlaps(Factory, Find("Батарея"));
        var only = Assert.Single(o);
        Assert.Equal("Закрыт", only.Other.Name);
        Assert.Equal("Закрыт", only.Winner.Name);
        Assert.DoesNotContain(o, x => x.Other.Name is "Слабый адаптер" or "Сеть, закрыт");

        var reverse = Assert.Single(Resolver.Overlaps(Factory, Find("Закрыт")), x => x.Other.Name == "Батарея");
        Assert.Equal("Закрыт", reverse.Winner.Name);
        Assert.Empty(Resolver.Overlaps(Factory, Find("Сеть, закрыт")));
    }
}
