using LegionModes.Core.Model;

namespace LegionModes.Core.Engine;

public sealed class ProfileEngine        // not thread-safe: the caller serialises every call
{
    readonly IClock clock;
    ModesConfig config;
    readonly Dictionary<Guid, MatchClock> matches = [];
    readonly List<(OverrideKind Kind, Workload? Workload, DateTimeOffset Since)> overrides = [];
    Inputs? last;
    Profile? applied;
    DateTimeOffset? lidClosedAt;

    public ProfileEngine(ModesConfig config, IClock clock, bool autonomy = false)
    {
        this.config = config;
        this.clock = clock;
        if (autonomy) overrides.Add((OverrideKind.Autonomy, null, clock.Now));
    }

    public bool Autonomy => overrides.Any(o => o.Kind == OverrideKind.Autonomy);

    public bool IsSet(OverrideKind kind) => overrides.Any(o => o.Kind == kind);

    public Workload? ForcedWorkload => overrides.Where(o => o.Kind == OverrideKind.ForceWorkload).Select(o => o.Workload).LastOrDefault();

    public void Reconfigure(ModesConfig newConfig)
    {
        config = newConfig;
        var ids = newConfig.Profiles.Select(p => p.Id).ToHashSet();
        foreach (var id in matches.Keys.Where(id => !ids.Contains(id)).ToArray()) matches.Remove(id);
    }

    public void Set(OverrideKind kind, Workload? workload = null)
    {
        if (kind == OverrideKind.Autonomy && last?.Source != PowerSource.Battery)
            throw new InvalidOperationException("Autonomy can be switched on only while the power source is the battery.");
        if (kind == OverrideKind.ForceWorkload && workload is null)
            throw new ArgumentException("ForceWorkload needs a workload.", nameof(workload));
        overrides.RemoveAll(o => o.Kind == kind);
        overrides.Add((kind, kind == OverrideKind.ForceWorkload ? workload : null, clock.Now));
    }

    public void Clear(OverrideKind kind) => overrides.RemoveAll(o => o.Kind == kind);

    public void OnResume(Inputs inputs) => lidClosedAt = inputs.Lid == LidState.Closed ? clock.Now : null;

    static readonly OverrideKind[] LiftedByChange =
        [OverrideKind.ForceWorkload, OverrideKind.StayAwake, OverrideKind.RapidCharge, OverrideKind.ChargeFull, OverrideKind.Pause];

    public Decision Update(Inputs inputs)
    {
        var now = clock.Now;
        var changed = last is not null && (last.Source != inputs.Source || last.Lid != inputs.Lid);
        if (changed) overrides.RemoveAll(o => LiftedByChange.Contains(o.Kind));
        if (inputs.Source != PowerSource.Battery) overrides.RemoveAll(o => o.Kind == OverrideKind.Autonomy);
        overrides.RemoveAll(o => o.Kind == OverrideKind.Pause && now >= o.Since.AddMinutes(config.PauseMinutes));
        last = inputs;

        if (inputs.Lid == LidState.Closed) lidClosedAt ??= now; else lidClosedAt = null;

        var load = overrides.Where(o => o.Kind == OverrideKind.ForceWorkload).Select(o => o.Workload).LastOrDefault() ?? inputs.Load;
        var axes = new AxisState(inputs.Source, inputs.Lid, load,
            inputs.ExternalDisplay ? MonitorState.Connected : MonitorState.None);

        var matching = new List<Profile>();
        var pending = new List<PendingMatch>();
        DateTimeOffset? next = null;
        void Soonest(DateTimeOffset? t) { if (t is { } v && (next is null || v < next)) next = v; }
        foreach (var p in config.Profiles.Where(p => p.Kind == ProfileKind.Automatic))
        {
            if (!matches.TryGetValue(p.Id, out var m)) matches[p.Id] = m = new MatchClock();
            var holds = p.Conditions.Matches(axes);
            if (changed) m.Reset(holds, now);
            m.Step(holds, now, p.DelaySec, p.HoldSec);
            if (m.Matched) matching.Add(p);
            if (m.PendingAt(p.DelaySec) is { } at) { pending.Add(new PendingMatch(p, at)); Soonest(at); }
            if (m.Matched && !m.Holds) Soonest(m.HoldUntil);
        }

        Profile profile;
        string reason;
        if (Autonomy) { profile = config.Autonomy; reason = Tr.T("включена вручную"); }
        else
        {
            profile = Resolver.Select(config, matching);
            var m = profile.Kind == ProfileKind.Automatic ? matches[profile.Id] : null;
            reason = m is null ? Tr.T("ни один профиль не подходит")
                : m.Holds ? Tr.T("условия выполнены") : Tr.F("удержание до {0:HH:mm:ss}", m.HoldUntil);
        }

        var pauseOverride = overrides.FirstOrDefault(o => o.Kind == OverrideKind.Pause);
        var paused = pauseOverride.Kind == OverrideKind.Pause;
        if (paused) Soonest(pauseOverride.Since.AddMinutes(config.PauseMinutes));

        var (parameters, notes) = Fold(Resolver.Effective(config, profile), inputs);

        DateTimeOffset? forcedSleepAt = null;
        if (lidClosedAt is { } closed && !inputs.ExternalDisplay && parameters.Timers.Value.ForcedSleepMin is > 0 and var mins)
            forcedSleepAt = closed.AddMinutes(mins);

        Transition? transition = null;
        if (!paused && applied?.Id != profile.Id)
        {
            transition = new Transition(applied, profile);
            applied = profile;
        }

        return new Decision(axes, profile, reason, parameters, notes, paused,
            overrides.Any(o => o.Kind == OverrideKind.DGpu), forcedSleepAt,
            overrides.Select(o => new ActiveOverride(o.Kind, o.Workload, o.Since,
                o.Kind == OverrideKind.Pause ? o.Since.AddMinutes(config.PauseMinutes) : null)).ToArray(),
            pending, next, transition);
    }

    (EffectiveParameters, IReadOnlyList<RuleNote>) Fold(EffectiveParameters e, Inputs inputs)
    {
        var notes = new List<RuleNote>();

        if (overrides.Any(o => o.Kind == OverrideKind.StayAwake))
        {
            var t = e.Timers.Value;
            e = e with { Timers = e.Timers with { Value = new IdleTimers(t.Dim is { Minutes: 0 } ? t.Dim : null, 0, 0, 0, null) } };
        }

        var charge = overrides.LastOrDefault(o => o.Kind is OverrideKind.RapidCharge or OverrideKind.ChargeFull);
        if (charge.Kind == OverrideKind.RapidCharge) e = e with { Charging = e.Charging with { Value = ChargingMode.Rapid } };
        else if (charge.Kind == OverrideKind.ChargeFull) e = e with { Charging = e.Charging with { Value = ChargingMode.Normal } };

        if (e.Gpu.Value == GpuMode.HybridIgpuOnly && inputs.ExternalDisplay)
        {
            e = e with { Gpu = e.Gpu with { Value = GpuMode.Hybrid } };
            notes.Add(RuleNote.ExternalMonitorKeepsHybrid);
        }
        if (e.External.Value.Projection == Projection.InternalOnly && inputs.Lid == LidState.Closed)
        {
            e = e with { External = e.External with { Value = e.External.Value with { Projection = Projection.DoNotTouch } } };
            notes.Add(RuleNote.LidClosedSkipsInternalOnly);
        }
        return (e, notes);
    }
}
