using LegionModes.Core.Model;

namespace LegionModes.Core.Status;

// What the laptop's firmware says (LENOVO_GAMEZONE_DATA, read by the plugin itself, never through LLT's cached feature values), turned into
// the plugin's own terms so the Status tab can set it beside what the profile asked for. Codes measured on a Legion Pro 7 16ARX8H (82WS) on 2026-10-24: iGPU
// mode 0 / 1 / 2 = Hybrid / integrated only / Hybrid auto, G-Sync flag 1 = discrete only, smart fan mode 1 / 2 / 3 = Quiet / Balance /
// Performance and 255 = the custom (GodMode) mode.
public sealed record FirmwareGpu(bool DiscreteOnly, GpuMode? Mode);

public enum PowerKind { Unknown, Quiet, Balance, Performance, Extreme, Custom }

public sealed record FirmwarePower(PowerKind Kind, int Code);

public static class FirmwareFacts
{
    public static FirmwareGpu? Gpu(int? gsync, int? igpuMode)
    {
        if (gsync is null || igpuMode is null) return null;
        if (gsync == 1) return new FirmwareGpu(true, null);
        return igpuMode switch
        {
            0 => new FirmwareGpu(false, GpuMode.Hybrid),
            1 => new FirmwareGpu(false, GpuMode.HybridIgpuOnly),
            2 => new FirmwareGpu(false, GpuMode.HybridAuto),
            _ => null,
        };
    }

    public static FirmwarePower? Power(int? smartFan) => smartFan switch
    {
        null => null,
        1 => new(PowerKind.Quiet, 1),
        2 => new(PowerKind.Balance, 2),
        3 => new(PowerKind.Performance, 3),
        224 => new(PowerKind.Extreme, 224),
        255 => new(PowerKind.Custom, 255),
        var c => new(PowerKind.Unknown, c.Value),
    };

    // Whether the firmware state is what the profile asked for. Own preset = the custom mode; a firmware mode = that mode.
    public static bool PowerMatches(FirmwarePower actual, PowerSetting asked) => asked.Firmware switch
    {
        null => actual.Kind == PowerKind.Custom,
        FirmwareMode.Quiet => actual.Kind == PowerKind.Quiet,
        FirmwareMode.Balance => actual.Kind == PowerKind.Balance,
        _ => actual.Kind == PowerKind.Performance,
    };

    public static bool GpuMatches(FirmwareGpu actual, GpuMode asked) => !actual.DiscreteOnly && actual.Mode == asked;
}
