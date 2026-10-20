using System.Text.Json.Serialization;

namespace LegionModes.Core.Model;

public enum MonitorState { None, Connected }

public readonly record struct AxisState(PowerSource Source, LidState Lid, Workload Load, MonitorState Monitor);

// A set of axis values a profile applies to; a null axis matches any value.
public sealed record Conditions(PowerSource? Source = null, LidState? Lid = null, Workload? Load = null, MonitorState? Monitor = null)
{
    [JsonIgnore]
    public int Specificity => (Source is null ? 0 : 1) + (Lid is null ? 0 : 1) + (Load is null ? 0 : 1) + (Monitor is null ? 0 : 1);

    public bool Matches(AxisState s) => (Source is null || Source == s.Source) && (Lid is null || Lid == s.Lid)
        && (Load is null || Load == s.Load) && (Monitor is null || Monitor == s.Monitor);

    public bool IsSubsetOf(Conditions o) => (Source is null || Source == o.Source) && (Lid is null || Lid == o.Lid)
        && (Load is null || Load == o.Load) && (Monitor is null || Monitor == o.Monitor);

    public bool CompatibleWith(Conditions o) => (Source is null || o.Source is null || Source == o.Source)
        && (Lid is null || o.Lid is null || Lid == o.Lid) && (Load is null || o.Load is null || Load == o.Load)
        && (Monitor is null || o.Monitor is null || Monitor == o.Monitor);
}

public enum ProfileKind { Root, Automatic, Manual }
