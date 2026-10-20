using LegionModes.Core;
using LegionModes.Core.Model;
using Wpf.Ui.Common;

namespace LegionModes.Plugin.Ui;

// Russian names and icons shared by the tabs (parameter names as refs/mode-defaults.md numbers them).
internal static class Texts
{
    public static IReadOnlyDictionary<ParameterId, string> Parameter => new Dictionary<ParameterId, string>
    {
        [ParameterId.Power] = Tr.T("Мощность и вентиляторы"),
        [ParameterId.Gpu] = Tr.T("Режим видеокарты"),
        [ParameterId.Panel] = Tr.T("Частота встроенного экрана"),
        [ParameterId.External] = Tr.T("Внешний монитор"),
        [ParameterId.Charging] = Tr.T("Зарядка"),
        [ParameterId.Usb] = Tr.T("USB во сне"),
        [ParameterId.Processor] = Tr.T("Процессор и схема Windows"),
        [ParameterId.Timers] = Tr.T("Таймеры простоя"),
        [ParameterId.Lighting] = Tr.T("Подсветка"),
    };

    public static readonly IReadOnlyDictionary<ParameterId, SymbolRegular> ParameterIcon = new Dictionary<ParameterId, SymbolRegular>
    {
        [ParameterId.Power] = SymbolRegular.Gauge24,
        [ParameterId.Gpu] = SymbolRegular.DeveloperBoard24,
        [ParameterId.Panel] = SymbolRegular.Laptop24,
        [ParameterId.External] = SymbolRegular.Desktop24,
        [ParameterId.Charging] = SymbolRegular.BatteryCharge24,
        [ParameterId.Usb] = SymbolRegular.UsbPlug24,
        [ParameterId.Processor] = SymbolRegular.Flash24,
        [ParameterId.Timers] = SymbolRegular.Timer24,
        [ParameterId.Lighting] = SymbolRegular.Keyboard24,
    };

    // The icon of a profile: its most significant condition (source, then lid, then workload, then monitor).
    public static SymbolRegular ProfileIcon(Profile p) => p.Kind switch
    {
        ProfileKind.Root => SymbolRegular.Globe24,
        ProfileKind.Manual => SymbolRegular.LeafTwo24,
        _ => p.Conditions switch
        {
            { Source: PowerSource.Ac } => SymbolRegular.PlugConnected24,
            { Source: PowerSource.Weak } => SymbolRegular.UsbPlug24,
            { Source: PowerSource.Battery } => SymbolRegular.Battery524,
            { Lid: not null } => SymbolRegular.Laptop24,
            { Load: { } w } => WorkloadIcon(w),
            _ => SymbolRegular.Desktop24,
        },
    };

    public static SymbolRegular WorkloadIcon(Workload w) => w switch
    {
        Workload.Heavy => SymbolRegular.Flash24,
        Workload.Ai => SymbolRegular.BrainCircuit24,
        Workload.Work => SymbolRegular.Desktop24,
        _ => SymbolRegular.WeatherMoon24,
    };

    public static string Seconds(int s) => s % 60 == 0 && s > 0 ? Tr.F("{0} мин", s / 60) : Tr.F("{0} с", s);

    // "Сеть, открыт · удержание 1 мин · задаёт 4 параметра"
    public static string ProfileSummary(Profile p)
    {
        var parts = new List<string>();
        parts.Add(p.Kind switch
        {
            ProfileKind.Root => Tr.T("Действует всегда, когда не подходит ни один другой"),
            ProfileKind.Manual => Tr.T("Включается вручную, только от батареи"),
            _ => Tr.F("Условия: {0}", ProfileNames.FromConditionsShown(p.Conditions)),
        });
        if (p.DelaySec > 0) parts.Add(Tr.F("задержка {0}", Seconds(p.DelaySec)));
        if (p.HoldSec > 0) parts.Add(Tr.F("удержание {0}", Seconds(p.HoldSec)));
        var set = Enum.GetValues<ParameterId>().Count(p.Set.IsSet);
        parts.Add(set == 0 ? Tr.T("всё наследует") : Tr.F("задаёт {0}", Plural(set, "параметр", "параметра", "параметров")));
        if (p.Pipelines.Count > 0) parts.Add(Plural(p.Pipelines.Count, "действие", "действия", "действий"));
        return string.Join(" · ", parts);
    }

    public static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        var word = mod100 is >= 11 and <= 14 ? many : mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
        return $"{n} {Tr.T(word)}";
    }
}
