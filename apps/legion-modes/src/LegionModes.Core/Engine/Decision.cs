using LegionModes.Core.Model;

namespace LegionModes.Core.Engine;

public sealed record Inputs(PowerSource Source, LidState Lid, Workload Load, bool ExternalDisplay);
public enum OverrideKind { ForceWorkload, Autonomy, StayAwake, RapidCharge, ChargeFull, DGpu, Pause }
public sealed record ActiveOverride(OverrideKind Kind, Workload? Workload, DateTimeOffset Since, DateTimeOffset? Until);
public enum RuleNote { ExternalMonitorKeepsHybrid, LidClosedSkipsInternalOnly }
public sealed record PendingMatch(Profile Profile, DateTimeOffset At);        // a delay running
public sealed record Transition(Profile? From, Profile To)
{
    // Two transitions in a row: the first From to the last To. null when the burst ended in the profile it began in.
    public static Transition? Chain(Transition? first, Transition? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return first.From?.Id == second.To.Id ? null : new Transition(first.From, second.To);
    }
}
public sealed record Decision(
    AxisState Axes,                       // ForceWorkload applied
    Profile Profile, string Reason,       // in force and why, Russian (shown on Status)
    EffectiveParameters Parameters,       // overrides and built-in rules folded in
    IReadOnlyList<RuleNote> Notes, bool Paused, bool DGpu, DateTimeOffset? ForcedSleepAt,
    IReadOnlyList<ActiveOverride> Overrides, IReadOnlyList<PendingMatch> Pending,
    DateTimeOffset? NextChangeAt,         // earliest delay end, hold end or pause end: the plugin re-evaluates then
    Transition? Transition);              // non-null when not paused and the profile in force differs from the last applied one
