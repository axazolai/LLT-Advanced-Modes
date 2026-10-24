using LegionModes.Core.Engine;
using LegionModes.Core.Ipc;
using LegionModes.Core.Model;

namespace LegionModes.Core.Quick;

public sealed record QuickResult(bool Handled, string? Error);

public static class QuickActions
{
    static readonly Dictionary<QuickActionKind, string> Args = new()
    {
        [QuickActionKind.WorkloadAuto] = "workload-auto",
        [QuickActionKind.WorkloadHeavy] = "workload-heavy",
        [QuickActionKind.WorkloadAi] = "workload-ai",
        [QuickActionKind.WorkloadWork] = "workload-work",
        [QuickActionKind.WorkloadIdle] = "workload-idle",
        [QuickActionKind.Autonomy] = "autonomy",
        [QuickActionKind.StayAwake] = "stay-awake",
        [QuickActionKind.RapidCharge] = "rapid-charge",
        [QuickActionKind.ChargeFull] = "charge-full",
        [QuickActionKind.Microphone] = "microphone",
        [QuickActionKind.Pause] = "pause",
        [QuickActionKind.DGpu] = "dgpu",
    };

    public static string Arg(QuickActionKind k) => Args[k];

    public static QuickActionKind? Parse(string arg) =>
        Args.Where(p => p.Value == arg).Select(p => (QuickActionKind?)p.Key).FirstOrDefault();

    public static string AutonomyBatteryOnly => Tr.T("Autonomy is available on battery only");

    // Engine-side actions. Microphone and DGpu are done by the plugin: Handled = false.
    public static QuickResult Apply(ProfileEngine engine, QuickActionKind k)
    {
        switch (k)
        {
            case QuickActionKind.WorkloadAuto: engine.Clear(OverrideKind.ForceWorkload); break;
            case QuickActionKind.WorkloadHeavy: engine.Set(OverrideKind.ForceWorkload, Workload.Heavy); break;
            case QuickActionKind.WorkloadAi: engine.Set(OverrideKind.ForceWorkload, Workload.Ai); break;
            case QuickActionKind.WorkloadWork: engine.Set(OverrideKind.ForceWorkload, Workload.Work); break;
            case QuickActionKind.WorkloadIdle: engine.Set(OverrideKind.ForceWorkload, Workload.Idle); break;
            case QuickActionKind.Autonomy:
                if (engine.IsSet(OverrideKind.Autonomy)) { engine.Clear(OverrideKind.Autonomy); break; }
                try { engine.Set(OverrideKind.Autonomy); }
                catch (InvalidOperationException) { return new QuickResult(true, AutonomyBatteryOnly); }
                break;
            case QuickActionKind.StayAwake: Toggle(engine, OverrideKind.StayAwake); break;
            case QuickActionKind.RapidCharge: Toggle(engine, OverrideKind.RapidCharge); break;
            case QuickActionKind.ChargeFull: Toggle(engine, OverrideKind.ChargeFull); break;
            case QuickActionKind.Pause: Toggle(engine, OverrideKind.Pause); break;
            default: return new QuickResult(false, null);
        }
        return new QuickResult(true, null);
    }

    static void Toggle(ProfileEngine engine, OverrideKind kind)
    {
        if (engine.IsSet(kind)) engine.Clear(kind); else engine.Set(kind);
    }
}
