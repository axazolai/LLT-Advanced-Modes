namespace LegionModes.Core.Model;

public sealed record Profile(Guid Id, string Name, ProfileKind Kind, Conditions Conditions, int DelaySec, int HoldSec,
    Parameters Set, IReadOnlyList<Pipeline> Pipelines, IReadOnlyList<PipelineSlot> Order,
    IReadOnlyDictionary<string, string>? Names = null);
