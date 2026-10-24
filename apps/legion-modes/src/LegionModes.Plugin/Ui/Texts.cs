using LegionModes.Core;
using LegionModes.Core.Model;
using Wpf.Ui.Common;

namespace LegionModes.Plugin.Ui;

// Russian names and icons shared by the tabs (parameter names as refs/mode-defaults.md numbers them).
internal static class Texts
{
    public static IReadOnlyDictionary<ParameterId, string> Parameter => new Dictionary<ParameterId, string>
    {
        [ParameterId.Power] = Tr.T("Power and fans"),
        [ParameterId.Gpu] = Tr.T("GPU mode"),
        [ParameterId.Panel] = Tr.T("Built-in display refresh rate"),
        [ParameterId.External] = Tr.T("External monitor"),
        [ParameterId.Charging] = Tr.T("Charging"),
        [ParameterId.Usb] = Tr.T("USB in sleep"),
        [ParameterId.Processor] = Tr.T("Processor and Windows plan"),
        [ParameterId.Timers] = Tr.T("Idle timers"),
        [ParameterId.Lighting] = Tr.T("Lighting"),
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

    public static string Seconds(int s) => s % 60 == 0 && s > 0 ? Tr.F("{0} min", s / 60) : Tr.F("{0} s", s);

    // "Сеть, открыт · удержание 1 мин · задаёт 4 параметра"
    // Levels beyond the first: one dot per level in the left gutter of the card, 16 px of indent per level (the list shows the hierarchy).
    public static int Level(Profile p) => p.Kind == ProfileKind.Automatic && p.Conditions.Specificity > 1 ? p.Conditions.Specificity - 1 : 0;

    public const double LevelStep = 16;

    // The dots sit 3 px left of the centre of their column (owner 2026-10-22, by his guide lines).
    public const double LevelDotShift = -3;

    public static double LevelIndent(Profile p) => LevelStep * Level(p);

    public static string ProfileSummary(Profile p, IReadOnlyList<Profile>? parents = null)
    {
        var parts = new List<string>();
        parts.Add(p.Kind switch
        {
            ProfileKind.Root => Tr.T("In force whenever no other profile matches"),
            ProfileKind.Manual => Tr.T("Switched on by hand, on battery only"),
            _ => Tr.F("Conditions: {0}", ProfileNames.FromConditionsShown(p.Conditions)),
        });
        if (p.DelaySec > 0) parts.Add(Tr.F("delay {0}", Seconds(p.DelaySec)));
        if (p.HoldSec > 0) parts.Add(Tr.F("hold {0}", Seconds(p.HoldSec)));
        var set = Enum.GetValues<ParameterId>().Count(p.Set.IsSet);
        if (parents is { Count: > 0 }) parts.Add(Tr.F("inherits from: {0}", string.Join(", ", parents.Select(ProfileNames.Display))));
        parts.Add(set == 0 ? Tr.T("inherits everything") : Tr.F("sets {0}", Plural(set, "parameter", "parameters", "parameters|2")));
        if (p.Pipelines.Count > 0) parts.Add(Plural(p.Pipelines.Count, "action", "actions|2", "actions"));
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
