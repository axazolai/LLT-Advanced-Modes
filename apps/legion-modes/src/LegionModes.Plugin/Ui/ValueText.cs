using LegionModes.Core;
using LegionModes.Core.Model;

namespace LegionModes.Plugin.Ui;

// The Russian text of a parameter value: the main value line of a parameter card, the effective-value lines.
internal static class ValueText
{
    public static string Of(ParameterId id, object? value, string sourceName) => (id, value) switch
    {
        (_, null) => "—",
        (ParameterId.Power, PowerSetting p) => p.Firmware is { } f ? Firmware(f) : Tr.F("Свой пресет «{0}»", sourceName),
        (ParameterId.Gpu, GpuMode g) => Gpu(g),
        (ParameterId.Panel, PanelRate r) => Panel(r),
        (ParameterId.External, ExternalMonitor m) => $"{Projection(m.Projection)} · {ExternalRate(m.Rate)}",
        (ParameterId.Charging, ChargingMode c) => Charging(c),
        (ParameterId.Usb, UsbPower u) => Usb(u),
        (ParameterId.Processor, ProcessorScheme s) => Processor(s),
        (ParameterId.Timers, IdleTimers t) => Timers(t),
        (ParameterId.Lighting, Lighting l) => l.Brightness == 0 ? Tr.F("профиль {0} · выключена", l.Profile) : Tr.F("профиль {0} · яркость {1}", l.Profile, l.Brightness),
        _ => value.ToString() ?? "—",
    };

    public static string Firmware(FirmwareMode f) => f switch
    {
        FirmwareMode.Quiet => Tr.T("Тихий"),
        FirmwareMode.Balance => Tr.T("Баланс"),
        _ => Tr.T("Производительность"),
    };

    public static string Gpu(GpuMode g) => g switch
    {
        GpuMode.Hybrid => Tr.T("Гибридный"),
        GpuMode.HybridIgpuOnly => Tr.T("Только встроенная"),
        _ => Tr.T("Гибридный, авто"),
    };

    public static string Panel(PanelRate r) => r.Kind switch
    {
        PanelRateKind.DoNotTouch => Tr.T("Не трогать"),
        PanelRateKind.Drr => Tr.T("DRR (120 Гц, до 60)"),
        _ => Tr.F("{0} Гц", r.Hz),
    };

    public static string Projection(Projection p) => p switch
    {
        Core.Model.Projection.DoNotTouch => Tr.T("Не трогать"),
        Core.Model.Projection.Extend => Tr.T("Расширить"),
        Core.Model.Projection.ExternalOnly => Tr.T("Только внешний"),
        Core.Model.Projection.Duplicate => Tr.T("Дублировать"),
        _ => Tr.T("Только встроенный"),
    };

    public static string ExternalRate(ExternalRate r) => r.Kind switch
    {
        ExternalRateKind.DoNotTouch => Tr.T("частота не трогать"),
        ExternalRateKind.Maximum => Tr.T("максимальная частота"),
        _ => Tr.F("{0} Гц", r.Hz),
    };

    public static string Charging(ChargingMode c) => c switch
    {
        ChargingMode.DoNotTouch => Tr.T("Не трогать"),
        ChargingMode.Conservation => Tr.T("Сбережение"),
        ChargingMode.Normal => Tr.T("Обычная"),
        _ => Tr.T("Быстрая"),
    };

    public static string Usb(UsbPower u) => u switch
    {
        UsbPower.Off => Tr.T("Выключено"),
        UsbPower.WhenSleeping => Tr.T("Во сне"),
        _ => Tr.T("Всегда"),
    };

    public static string Boost(BoostMode b) => b switch
    {
        BoostMode.DoNotTouch => Tr.T("не трогать"),
        BoostMode.Enabled => Tr.T("включён"),
        _ => Tr.T("выключен"),
    };

    public static string WindowsMode(WindowsPowerMode m) => m switch
    {
        WindowsPowerMode.DoNotTouch => Tr.T("не трогать"),
        WindowsPowerMode.BestPowerEfficiency => Tr.T("экономия"),
        WindowsPowerMode.Balanced => Tr.T("сбалансированный"),
        _ => Tr.T("производительность"),
    };

    public static string Processor(ProcessorScheme s) =>
        Tr.F("Турбо {0} · EPP {1} · Windows {2}", Boost(s.Boost), s.Epp is { } e ? e.ToString() : Tr.T("не трогать"), WindowsMode(s.PowerMode));

    public static string Minutes(int m) => m == 0 ? Tr.T("никогда") : Tr.F("{0} мин", m);

    public static string Timers(IdleTimers t)
    {
        var parts = new List<string>();
        parts.Add(t.Dim switch
        {
            null => Tr.T("без затемнения"),
            { Minutes: 0 } d => Tr.F("яркость {0} %", d.Brightness),
            var d => Tr.F("затемнение {0} мин → {1} %", d.Minutes, d.Brightness),
        });
        parts.Add(Tr.F("экран {0}", Minutes(t.DisplayOffMin)));
        parts.Add(Tr.F("сон {0}", Minutes(t.SleepMin)));
        parts.Add(Tr.F("гибернация {0}", Minutes(t.HibernateMin)));
        if (t.ForcedSleepMin is { } f) parts.Add(Tr.F("сон с закрытой крышкой через {0} мин", f));
        return string.Join(" · ", parts);
    }
}
