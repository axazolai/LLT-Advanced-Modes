using LegionModes.Core;
using LegionModes.Core.Model;

namespace LegionModes.Plugin.Ui;

// The Russian text of a parameter value: the main value line of a parameter card, the effective-value lines.
internal static class ValueText
{
    public static string Of(ParameterId id, object? value, string sourceName) => (id, value) switch
    {
        (_, null) => "—",
        (ParameterId.Power, PowerSetting p) => p.Firmware is { } f ? Firmware(f) : Tr.F("Own preset \"{0}\"", sourceName),
        (ParameterId.Gpu, GpuMode g) => Gpu(g),
        (ParameterId.Panel, PanelRate r) => Panel(r),
        (ParameterId.External, ExternalMonitor m) => $"{Projection(m.Projection)} · {ExternalRate(m.Rate)}",
        (ParameterId.Charging, ChargingMode c) => Charging(c),
        (ParameterId.Usb, UsbPower u) => Usb(u),
        (ParameterId.Processor, ProcessorScheme s) => Processor(s),
        (ParameterId.Timers, IdleTimers t) => Timers(t),
        (ParameterId.Lighting, Lighting l) => l.Brightness == 0 ? Tr.F("profile {0} · off", l.Profile) : Tr.F("profile {0} · brightness {1}", l.Profile, l.Brightness),
        _ => value.ToString() ?? "—",
    };

    public static string Firmware(FirmwareMode f) => f switch
    {
        FirmwareMode.Quiet => Tr.T("Quiet"),
        FirmwareMode.Balance => Tr.T("Balance"),
        _ => Tr.T("Performance"),
    };

    public static string Gpu(GpuMode g) => g switch
    {
        GpuMode.Hybrid => Tr.T("Hybrid"),
        GpuMode.HybridIgpuOnly => Tr.T("Integrated only"),
        _ => Tr.T("Hybrid, auto"),
    };

    public static string Panel(PanelRate r) => r.Kind switch
    {
        PanelRateKind.DoNotTouch => Tr.T("Do not touch"),
        PanelRateKind.Drr => Tr.T("DRR (120 Hz, down to 60)"),
        _ => Tr.F("{0} Hz", r.Hz),
    };

    public static string Projection(Projection p) => p switch
    {
        Core.Model.Projection.DoNotTouch => Tr.T("Do not touch"),
        Core.Model.Projection.Extend => Tr.T("Extend"),
        Core.Model.Projection.ExternalOnly => Tr.T("External only"),
        Core.Model.Projection.Duplicate => Tr.T("Duplicate"),
        _ => Tr.T("Built-in only"),
    };

    public static string ExternalRate(ExternalRate r) => r.Kind switch
    {
        ExternalRateKind.DoNotTouch => Tr.T("refresh rate untouched"),
        ExternalRateKind.Maximum => Tr.T("maximum refresh rate"),
        _ => Tr.F("{0} Hz", r.Hz),
    };

    public static string Charging(ChargingMode c) => c switch
    {
        ChargingMode.DoNotTouch => Tr.T("Do not touch"),
        ChargingMode.Conservation => Tr.T("Conservation"),
        ChargingMode.Normal => Tr.T("Normal"),
        _ => Tr.T("Fast"),
    };

    public static string Usb(UsbPower u) => u switch
    {
        UsbPower.Off => Tr.T("Off"),
        UsbPower.WhenSleeping => Tr.T("While sleeping"),
        _ => Tr.T("Always"),
    };

    public static string Boost(BoostMode b) => b switch
    {
        BoostMode.DoNotTouch => Tr.T("do not touch"),
        BoostMode.Enabled => Tr.T("on|2"),
        _ => Tr.T("off"),
    };

    public static string WindowsMode(WindowsPowerMode m) => m switch
    {
        WindowsPowerMode.DoNotTouch => Tr.T("do not touch"),
        WindowsPowerMode.BestPowerEfficiency => Tr.T("power saver"),
        WindowsPowerMode.Balanced => Tr.T("balanced"),
        _ => Tr.T("performance"),
    };

    public static string Processor(ProcessorScheme s) =>
        Tr.F("Turbo {0} · EPP {1} · Windows {2}", Boost(s.Boost), s.Epp is { } e ? e.ToString() : Tr.T("do not touch"), WindowsMode(s.PowerMode));

    public static string Minutes(int m) => m == 0 ? Tr.T("never") : Tr.F("{0} min", m);

    public static string Timers(IdleTimers t)
    {
        var parts = new List<string>();
        parts.Add(t.Dim switch
        {
            null => Tr.T("no dimming"),
            { Minutes: 0 } d => Tr.F("brightness {0} %", d.Brightness),
            var d => Tr.F("dim after {0} min → {1} %", d.Minutes, d.Brightness),
        });
        parts.Add(Tr.F("screen {0}", Minutes(t.DisplayOffMin)));
        parts.Add(Tr.F("sleep {0}", Minutes(t.SleepMin)));
        parts.Add(Tr.F("hibernate {0}", Minutes(t.HibernateMin)));
        if (t.ForcedSleepMin is { } f) parts.Add(Tr.F("sleep with the lid closed after {0} min", f));
        return string.Join(" · ", parts);
    }
}
