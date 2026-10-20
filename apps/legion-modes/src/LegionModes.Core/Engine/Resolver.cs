using LegionModes.Core.Model;

namespace LegionModes.Core.Engine;

public sealed record Sourced<T>(T Value, Profile Source);

public sealed record EffectiveParameters(Sourced<PowerSetting> Power, Sourced<GpuMode> Gpu, Sourced<PanelRate> Panel,
    Sourced<ExternalMonitor> External, Sourced<ChargingMode> Charging, Sourced<UsbPower> Usb,
    Sourced<ProcessorScheme> Processor, Sourced<IdleTimers> Timers, Sourced<Lighting> Lighting);

// Wins: runs on its event; Off: switched off by this profile; Blocked: switched off higher up (BlockedBy), not reachable here.
public enum PipelineState { Wins, Off, Blocked }

public sealed record EffectivePipeline(Pipeline Pipeline, Profile Source, PipelineState State, Profile? BlockedBy);

public sealed record Overlap(Profile Other, Profile Winner);

public static class Resolver
{
    public static IReadOnlyList<Profile> Ancestors(ModesConfig c, Profile p)
    {
        if (p.Kind == ProfileKind.Root) return [];
        if (p.Kind == ProfileKind.Manual) return [c.Root];
        var found = c.Profiles
            .Select((q, i) => (q, i))
            .Where(x => x.q.Kind == ProfileKind.Automatic && x.q.Id != p.Id && x.q.Conditions.IsSubsetOf(p.Conditions))
            .OrderByDescending(x => x.q.Conditions.Specificity).ThenBy(x => x.i)
            .Select(x => x.q);
        return found.Append(c.Root).ToArray();
    }

    public static EffectiveParameters Effective(ModesConfig c, Profile p)
    {
        var chain = new[] { p }.Concat(Ancestors(c, p)).ToArray();
        Sourced<T> Pick<T>(ParameterId id)
        {
            foreach (var q in chain)
                if (q.Set.Get(id) is T v) return new Sourced<T>(v, q);
            throw new InvalidOperationException($"Parameter {id} is not set by the profile or any ancestor (the root sets all).");
        }
        return new EffectiveParameters(Pick<PowerSetting>(ParameterId.Power), Pick<GpuMode>(ParameterId.Gpu),
            Pick<PanelRate>(ParameterId.Panel), Pick<ExternalMonitor>(ParameterId.External),
            Pick<ChargingMode>(ParameterId.Charging), Pick<UsbPower>(ParameterId.Usb),
            Pick<ProcessorScheme>(ParameterId.Processor), Pick<IdleTimers>(ParameterId.Timers),
            Pick<Lighting>(ParameterId.Lighting));
    }

    // The profiles this one inherits from directly: its ancestors that are no ancestor of another of its ancestors (the root
    // when there is none).
    public static IReadOnlyList<Profile> Parents(ModesConfig c, Profile p) => new Graph(c).Parents(p);

    // Ancestors and parents of one config, each worked out once (the runner resolves on every LLT event).
    sealed class Graph(ModesConfig c)
    {
        readonly Dictionary<Guid, IReadOnlyList<Profile>> _ancestors = [], _parents = [];

        public IReadOnlyList<Profile> Ancestors(Profile p) =>
            _ancestors.TryGetValue(p.Id, out var a) ? a : _ancestors[p.Id] = Resolver.Ancestors(c, p);

        public IReadOnlyList<Profile> Parents(Profile p)
        {
            if (_parents.TryGetValue(p.Id, out var known)) return known;
            var ancestors = Ancestors(p);
            return _parents[p.Id] = ancestors.Where(a => !ancestors.Any(b => b.Id != a.Id && IsUnder(b, a))).ToArray();
        }

        // `b` is `owner` or inherits from it.
        public bool IsUnder(Profile b, Profile owner) => b.Id == owner.Id || Ancestors(b).Any(x => x.Id == owner.Id);
    }

    // Every pipeline of the profile and of its ancestors runs on its event (owner 2026-10-20): nothing overrides another. An
    // inherited pipeline reaches the profile along a chain of parents in which no profile, its own included, switched it
    // off; it reaches when one such chain is open. One that reaches nowhere open is Blocked, at the end of the list, with
    // the nearest profile that switched it off. The profile's own slots order the rest and switch them off here (Off).
    public static IReadOnlyList<EffectivePipeline> Pipelines(ModesConfig c, Profile p)
    {
        var g = new Graph(c);
        var reached = new List<(Pipeline Pipe, Profile Source)>();
        var blocked = new List<EffectivePipeline>();
        foreach (var pipe in p.Pipelines)
            if (reached.All(a => a.Pipe.Id != pipe.Id)) reached.Add((pipe, p));
        var ancestors = g.Ancestors(p);
        foreach (var q in ancestors)
            foreach (var pipe in q.Pipelines)
            {
                if (reached.Any(a => a.Pipe.Id == pipe.Id) || blocked.Any(b => b.Pipeline.Id == pipe.Id)) continue;
                if (Reaches(g, pipe.Id, q, p, [])) reached.Add((pipe, q));
                else
                {
                    // The nearest profile that switched it off and still has it, so it can be switched on there; else the nearest.
                    var by = ancestors.FirstOrDefault(b => SwitchedOff(b, pipe.Id) && (b.Id == q.Id || (g.IsUnder(b, q) && Reaches(g, pipe.Id, q, b, []))))
                             ?? ancestors.FirstOrDefault(b => SwitchedOff(b, pipe.Id) && g.IsUnder(b, q)) ?? q;
                    blocked.Add(new(pipe, q, PipelineState.Blocked, by));
                }
            }

        var ordered = new List<(Pipeline Pipe, Profile Source, bool Enabled)>();
        foreach (var slot in p.Order)
        {
            var hit = reached.FindIndex(a => a.Pipe.Id == slot.Id);
            if (hit < 0 || ordered.Any(o => o.Pipe.Id == slot.Id)) continue;     // vanished, blocked or repeated slot
            ordered.Add((reached[hit].Pipe, reached[hit].Source, slot.Enabled));
        }
        foreach (var a in reached)
            if (ordered.All(o => o.Pipe.Id != a.Pipe.Id)) ordered.Add((a.Pipe, a.Source, true));

        return ordered.Select(o => new EffectivePipeline(o.Pipe, o.Source, o.Enabled ? PipelineState.Wins : PipelineState.Off, null))
            .Concat(blocked).ToArray();
    }

    // The slots to store for `p` after an edit: the edited ones, then p's earlier slots for the pipelines blocked in it now
    // (an «off» kept there still counts once the profile above switches the pipeline on again).
    public static IReadOnlyList<PipelineSlot> KeepBlockedSlots(ModesConfig c, Profile p, IEnumerable<PipelineSlot> edited)
    {
        var blocked = Pipelines(c, p).Where(e => e.State == PipelineState.Blocked).Select(e => e.Pipeline.Id).ToHashSet();
        var result = edited.ToList();
        foreach (var slot in p.Order)
            if (blocked.Contains(slot.Id) && result.All(s => s.Id != slot.Id)) result.Add(slot);
        return result;
    }

    static bool SwitchedOff(Profile p, Guid pipeline) => p.Order.FirstOrDefault(s => s.Id == pipeline) is { Enabled: false };

    // `owner`'s pipeline reaches `p` through a parent: the owner itself with the pipeline on, or a profile under the owner
    // that has it on and is reached. `seen` holds the profiles already answered (false while in progress).
    static bool Reaches(Graph g, Guid pipeline, Profile owner, Profile p, Dictionary<Guid, bool> seen)
    {
        if (seen.TryGetValue(p.Id, out var known)) return known;
        seen[p.Id] = false;
        var result = g.Parents(p).Any(q => !SwitchedOff(q, pipeline)
            && (q.Id == owner.Id || (g.IsUnder(q, owner) && Reaches(g, pipeline, owner, q, seen))));
        seen[p.Id] = result;
        return result;
    }

    public static IReadOnlyList<Overlap> Overlaps(ModesConfig c, Profile p)
    {
        if (p.Kind != ProfileKind.Automatic) return [];
        var pi = c.IndexOf(p);
        return c.Profiles
            .Where(q => q.Kind == ProfileKind.Automatic && q.Id != p.Id
                && q.Conditions.Specificity == p.Conditions.Specificity && q.Conditions.CompatibleWith(p.Conditions))
            .Select(q => new Overlap(q, c.IndexOf(q) < pi ? q : p))
            .ToArray();
    }

    public static Profile Select(ModesConfig c, IEnumerable<Profile> matching)
    {
        var best = matching.Where(m => m.Kind == ProfileKind.Automatic)
            .OrderByDescending(m => m.Conditions.Specificity).ThenBy(c.IndexOf).FirstOrDefault();
        return best ?? c.Root;
    }
}
