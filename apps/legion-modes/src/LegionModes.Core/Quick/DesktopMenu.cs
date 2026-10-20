using LegionModes.Core.Ipc;
using LegionModes.Core.Model;

namespace LegionModes.Core.Quick;

public sealed record MenuState(bool OnBattery, bool AutonomyOn, Workload? Forced, bool StayAwake, bool RapidCharge,
                               bool ChargeFull, bool Paused, bool MicrophoneMuted);

// Key is the registry name part and the order key; Action is null for a cascade.
public sealed record MenuItem(string Key, string Label, QuickActionKind? Action, IReadOnlyList<MenuItem> Children);

public static class DesktopMenu
{
    public static MenuItem Build(MenuState s)
    {
        static string Mark(bool on, string label) => on ? "✓ " + label : label;
        static MenuItem Leaf(QuickActionKind k, string label) => new(QuickActions.Arg(k), label, k, []);

        var items = new List<MenuItem>
        {
            new("workload", Tr.T("Нагрузка"), null,
            [
                Leaf(QuickActionKind.WorkloadAuto, Mark(s.Forced is null, Tr.T("Авто"))),
                Leaf(QuickActionKind.WorkloadHeavy, Mark(s.Forced == Workload.Heavy, Tr.T("Тяжёлая"))),
                Leaf(QuickActionKind.WorkloadAi, Mark(s.Forced == Workload.Ai, Tr.T("ИИ"))),
                Leaf(QuickActionKind.WorkloadWork, Mark(s.Forced == Workload.Work, Tr.T("Работа"))),
                Leaf(QuickActionKind.WorkloadIdle, Mark(s.Forced == Workload.Idle, Tr.T("Простой"))),
            ]),
        };
        if (s.OnBattery) items.Add(Leaf(QuickActionKind.Autonomy, Mark(s.AutonomyOn, Tr.T("Автономия"))));
        items.Add(Leaf(QuickActionKind.StayAwake, Mark(s.StayAwake, Tr.T("Не спать"))));
        items.Add(Leaf(QuickActionKind.RapidCharge, Mark(s.RapidCharge, Tr.T("Быстрая зарядка"))));
        items.Add(Leaf(QuickActionKind.ChargeFull, Mark(s.ChargeFull, Tr.T("Зарядить до 100 %"))));
        items.Add(Leaf(QuickActionKind.Microphone, s.MicrophoneMuted ? "✓ " + Tr.T("Микрофон выключен") : Tr.T("Микрофон")));
        items.Add(Leaf(QuickActionKind.Pause, Mark(s.Paused, Tr.T("Пауза"))));
        items.Add(Leaf(QuickActionKind.DGpu, "dGPU…"));
        return new MenuItem("root", Tr.T("Расширенные режимы"), null, items);
    }
}
