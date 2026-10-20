using System.Text.Json.Serialization;

namespace LegionModes.Core.Model;

public enum FirmwareMode { Quiet, Balance, Performance }

public sealed record PowerPreset(int CpuLongW, int CpuShortW, int CpuPeakW, int CpuCrossW, int CpuTempC, int CurveOptimizer,
    int DynamicBoostW, int ConfigurableTgpW, int GpuTempC, int TppW, int[] FanCurve, bool FanFullSpeed,
    int MinValueOffset, int MaxValueOffset)
{   // value equality over FanCurve: configs loaded from different files compare equal
    public bool Equals(PowerPreset? o) => o is not null && CpuLongW == o.CpuLongW && CpuShortW == o.CpuShortW
        && CpuPeakW == o.CpuPeakW && CpuCrossW == o.CpuCrossW && CpuTempC == o.CpuTempC && CurveOptimizer == o.CurveOptimizer
        && DynamicBoostW == o.DynamicBoostW && ConfigurableTgpW == o.ConfigurableTgpW && GpuTempC == o.GpuTempC
        && TppW == o.TppW && FanCurve.SequenceEqual(o.FanCurve) && FanFullSpeed == o.FanFullSpeed
        && MinValueOffset == o.MinValueOffset && MaxValueOffset == o.MaxValueOffset;
    public override int GetHashCode() => HashCode.Combine(CpuLongW, CpuShortW, CpuPeakW, ConfigurableTgpW,
        FanCurve.Aggregate(17, (h, v) => h * 31 + v), CurveOptimizer);
}

// The values of an own preset live in LLT's godmode.json as preset "AM · <name of the profile that sets the parameter>"
// (spec section 6); modes.json holds only this reference. PowerPreset carries values for the factory set and resets.
public sealed record PowerSetting(FirmwareMode? Firmware)      // null = the profile's own GodMode preset
{
    [JsonIgnore]
    public bool OwnPreset => Firmware is null;
    public static readonly PowerSetting Own = new((FirmwareMode?)null);
}

public static class PresetValidator
{
    // Firmware ranges; each stepper is bounded by [max(0, Min + MinValueOffset), Max + MaxValueOffset].
    public static IReadOnlyList<string> Errors(PowerPreset p)
    {
        var errors = new List<string>();
        void Stepper(string field, int value, Range r)
        {
            var min = Math.Max(0, r.Min + p.MinValueOffset);
            var max = r.Max + p.MaxValueOffset;
            if (value < min || value > max) errors.Add(Tr.F("{0}: {1} вне {2}..{3}", field, value, min, max));
        }
        void Member(string field, int value, int[] allowed)
        {
            if (!allowed.Contains(value)) errors.Add(Tr.F("{0}: {1} не из {2}", field, value, string.Join(", ", allowed)));
        }

        // The labels of LLT's GodMode window.
        Stepper(Tr.T("Длительный лимит мощности CPU, Вт"), p.CpuLongW, FirmwareRanges.CpuLong);
        Stepper(Tr.T("Кратковременный лимит мощности CPU, Вт"), p.CpuShortW, FirmwareRanges.CpuShort);
        Stepper(Tr.T("Пиковый лимит мощности CPU, Вт"), p.CpuPeakW, FirmwareRanges.CpuPeak);
        Stepper(Tr.T("Лимит мощности CPU при нагрузке на GPU, Вт"), p.CpuCrossW, FirmwareRanges.CpuCross);
        Stepper(Tr.T("Предел температуры CPU, °C"), p.CpuTempC, FirmwareRanges.CpuTemp);
        Stepper(Tr.T("Предел температуры GPU, °C"), p.GpuTempC, FirmwareRanges.GpuTemp);
        Stepper(Tr.T("Общая мощность (TPP), Вт"), p.TppW, FirmwareRanges.Tpp);
        Stepper("Curve Optimizer", p.CurveOptimizer, FirmwareRanges.CurveOptimizer);
        Member(Tr.T("Dynamic Boost, Вт"), p.DynamicBoostW, FirmwareRanges.DynamicBoost);
        Member(Tr.T("Настраиваемый TGP, Вт"), p.ConfigurableTgpW, FirmwareRanges.ConfigurableTgp);

        if (p.FanCurve is null || p.FanCurve.Length != FirmwareRanges.FanMinimum.Length)
            errors.Add(Tr.F("кривая вентиляторов: {0} точек, нужно {1}", p.FanCurve?.Length ?? 0, FirmwareRanges.FanMinimum.Length));
        else
            for (var i = 0; i < p.FanCurve.Length; i++)
                if (p.FanCurve[i] < FirmwareRanges.FanMinimum[i] || p.FanCurve[i] > 10)
                    errors.Add(Tr.F("кривая вентиляторов, точка {0}: {1} вне {2}..10", i + 1, p.FanCurve[i], FirmwareRanges.FanMinimum[i]));
        return errors;
    }
}

public enum PanelRateKind { DoNotTouch, Fixed, Drr }
public sealed record PanelRate(PanelRateKind Kind, int Hz = 0);      // Fixed: Hz in FirmwareRanges.PanelHz; Drr: 120 physical, 60 base
public sealed record ExternalMonitor(Projection Projection, ExternalRate Rate);
public enum BoostMode { DoNotTouch, Enabled, Disabled }             // Enabled is written as Windows index 2 (Aggressive)
public enum WindowsPowerMode { DoNotTouch, BestPowerEfficiency, Balanced, BestPerformance }
public sealed record ProcessorScheme(BoostMode Boost, int? Epp, WindowsPowerMode PowerMode);    // Epp null = do not touch, else 0..100
public sealed record DimStage(int Minutes, int Brightness);          // Minutes 0 = the profile's static brightness, no dimming
public sealed record IdleTimers(DimStage? Dim, int DisplayOffMin, int SleepMin, int HibernateMin, int? ForcedSleepMin);
                                                                    // minutes, 0 = never; Dim null = never; Hibernate counts from falling asleep

public enum ParameterId { Power = 1, Gpu, Panel, External, Charging, Usb, Processor, Timers, Lighting }   // spec order 1..9

public sealed record Parameters(PowerSetting? Power = null, GpuMode? Gpu = null, PanelRate? Panel = null,
    ExternalMonitor? External = null, ChargingMode? Charging = null, UsbPower? Usb = null, ProcessorScheme? Processor = null,
    IdleTimers? Timers = null, Lighting? Lighting = null)
{
    public bool IsSet(ParameterId id) => Get(id) is not null;
    public object? Get(ParameterId id) => id switch
    {
        ParameterId.Power => Power, ParameterId.Gpu => Gpu, ParameterId.Panel => Panel, ParameterId.External => External,
        ParameterId.Charging => Charging, ParameterId.Usb => Usb, ParameterId.Processor => Processor,
        ParameterId.Timers => Timers, ParameterId.Lighting => Lighting, _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
}

public sealed record Pipeline(Guid Id, string Key, string Llt);      // Key: PipelineKeys.Enter / Exit, or the LLT trigger key; Llt: LLT pipeline JSON
public static class PipelineKeys { public const string Enter = "enter", Exit = "exit"; }
public sealed record PipelineSlot(Guid Id, bool Enabled);           // a pipeline placed or switched off by this profile
