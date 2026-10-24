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
            new("workload", Tr.T("Workload"), null,
            [
                Leaf(QuickActionKind.WorkloadAuto, Mark(s.Forced is null, Tr.T("Auto"))),
                Leaf(QuickActionKind.WorkloadHeavy, Mark(s.Forced == Workload.Heavy, Tr.T("Heavy"))),
                Leaf(QuickActionKind.WorkloadAi, Mark(s.Forced == Workload.Ai, Tr.T("AI"))),
                Leaf(QuickActionKind.WorkloadWork, Mark(s.Forced == Workload.Work, Tr.T("Work"))),
                Leaf(QuickActionKind.WorkloadIdle, Mark(s.Forced == Workload.Idle, Tr.T("Idle"))),
            ]),
        };
        if (s.OnBattery) items.Add(Leaf(QuickActionKind.Autonomy, Mark(s.AutonomyOn, Tr.T("Autonomy"))));
        items.Add(Leaf(QuickActionKind.StayAwake, Mark(s.StayAwake, Tr.T("Stay awake"))));
        items.Add(Leaf(QuickActionKind.RapidCharge, Mark(s.RapidCharge, Tr.T("Rapid charge"))));
        items.Add(Leaf(QuickActionKind.ChargeFull, Mark(s.ChargeFull, Tr.T("Charge to 100 %"))));
        items.Add(Leaf(QuickActionKind.Microphone, s.MicrophoneMuted ? "✓ " + Tr.T("Microphone off") : Tr.T("Microphone")));
        items.Add(Leaf(QuickActionKind.Pause, Mark(s.Paused, Tr.T("Pause"))));
        items.Add(Leaf(QuickActionKind.DGpu, "dGPU…"));
        return new MenuItem("root", Tr.T("Advanced modes"), null, items);
    }
}
