using LegionModes.Core.Model;

namespace LegionModes.Core.Engine;

public static class ParameterDiff
{
    // The effective value ParameterDiff compares for a parameter (equal values mean "no change").
    public static object? ValueOf(EffectiveParameters e, ParameterId id) => id switch
    {
        ParameterId.Power => (e.Power.Value.Firmware, e.Power.Value.OwnPreset ? e.Power.Source.Id : (Guid?)null),
        ParameterId.Gpu => e.Gpu.Value,
        ParameterId.Panel => e.Panel.Value,
        ParameterId.External => e.External.Value,
        ParameterId.Charging => e.Charging.Value,
        ParameterId.Usb => e.Usb.Value,
        ParameterId.Processor => e.Processor.Value,
        ParameterId.Timers => e.Timers.Value,
        ParameterId.Lighting => e.Lighting.Value,
        _ => null,
    };

    // full or last null -> all nine. Power differs when the firmware mode differs or, for own presets, the source profile
    // differs (presets are activated by name); every other parameter by value.
    public static IReadOnlyList<ParameterId> Changed(EffectiveParameters? last, EffectiveParameters next, bool full)
    {
        var all = Enum.GetValues<ParameterId>();
        if (full || last is null) return all;
        return all.Where(id => id switch
        {
            ParameterId.Power => last.Power.Value.Firmware != next.Power.Value.Firmware
                || (next.Power.Value.OwnPreset && last.Power.Source.Id != next.Power.Source.Id),
            ParameterId.Gpu => last.Gpu.Value != next.Gpu.Value,
            ParameterId.Panel => last.Panel.Value != next.Panel.Value,
            ParameterId.External => last.External.Value != next.External.Value,
            ParameterId.Charging => last.Charging.Value != next.Charging.Value,
            ParameterId.Usb => last.Usb.Value != next.Usb.Value,
            ParameterId.Processor => last.Processor.Value != next.Processor.Value,
            ParameterId.Timers => last.Timers.Value != next.Timers.Value,
            ParameterId.Lighting => last.Lighting.Value != next.Lighting.Value,
            _ => false,
        }).ToArray();
    }
}
