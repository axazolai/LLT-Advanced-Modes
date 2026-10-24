using LegionModes.Core.Engine;
using LegionModes.Core.Ipc;
using LegionModes.Core.Model;
using LegionModes.Core.Programs;
using LegionModes.Core.Quick;
using LegionModes.Plugin.Status;

namespace LegionModes.Plugin.Ui;

// What the page and its tabs read and call. Runtime implements it inside LLT; the UI stand implements it with Core data.
public interface IModesHost
{
    StatusModel Status { get; }
    DeviceStatus Devices { get; }
    LiveFacts? Facts => null;                        // panel and charger state read live, null where there is no hardware
    DgpuLive? GpuLive => null;                       // the discrete card's live state; null where there is no LLT (the UI stand)
    ModesConfig Config { get; }
    ProgramsConfig Programs { get; }
    Decision? Last { get; }
    bool HasControl { get; }
    bool ControlHeld { get; }
    int MaxBrightness { get; }
    string LightingSnapshotPath { get; }
    string EditorLayoutPath { get; }                 // where the profile editor's block layout is kept (one for all profiles)
    event Action<Decision>? Decided;                 // raised on the UI dispatcher
    Task SaveModesAsync(ModesConfig c);              // throws ConfigInvalidException with the validator's errors
    Task SavePrograms(ProgramsConfig c);
    Task<string?> TakeControlAsync();
    Task<string?> ReturnControlAsync();
    Task ResetModesToFactoryAsync();                 // factory profiles, every AM · preset back to its factory values, one full apply
    QuickResult Quick(QuickActionKind k);
    void BeginGodModeEdit();
    void EndGodModeEdit();
    Task<IReadOnlyList<WakeRequest>> ReadWakeRequestsAsync();   // `powercfg /requests`; never throws
}

// Where the page finds its host: the running plugin, unless something else (the UI stand) provides one.
public static class ModesHost
{
    public static Func<IModesHost?> Source { get; set; } = () => Runtime.Current;
}
