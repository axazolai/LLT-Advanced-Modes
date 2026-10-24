using LegionModes.Core;
using LegionModes.Core.Model;
using LegionModes.Core.Programs;

namespace LegionModes.Plugin.Ui.ViewModels;

// The two configs the tabs edit, as the runtime holds them. Every tab reads from here and saves through here; a save
// refreshes the tabs that show that config (a tab's unsaved draft of the other config stays). The runtime validates every save before it writes (ConfigInvalidException), and keeps a copy of a
// file that failed to load before the first save over it.
public sealed class ModesDocument
{
    ModesDocument(IModesHost host)
    {
        Host = host;
        Modes = host.Config;
        Programs = host.Programs;
    }

    public static ModesDocument Load(IModesHost host) => new(host);

    public IModesHost Host { get; }
    public ModesConfig Modes { get; private set; }
    public ProgramsConfig Programs { get; private set; }
    public event Action? ModesChanged;
    public event Action? ProgramsChanged;

    // Returns null, or a line naming the tabs that failed to refresh.
    static string? Notify(Action? handlers)
    {
        var failed = new List<string>();
        foreach (var h in handlers?.GetInvocationList() ?? [])
        {
            try { ((Action)h)(); }
            catch (Exception e)
            {
                Log.Error("config changed handler", e);
                failed.Add(e.Message);
            }
        }
        return failed.Count == 0 ? null : Tr.F("saved, but the tab did not refresh: {0}", string.Join("; ", failed));
    }

    public async Task<string?> SaveModesAsync(ModesConfig config)
    {
        await Host.SaveModesAsync(config);
        Modes = config;
        return Notify(ModesChanged);
    }

    // The profiles back to the factory set. The tabs refresh even when the presets failed (modes.json is written by then).
    public async Task<string?> ResetModesToFactoryAsync()
    {
        Exception? presets = null;
        try { await Host.ResetModesToFactoryAsync(); }
        catch (InvalidOperationException e) { presets = e; }
        Modes = Host.Config;
        var warn = Notify(ModesChanged);
        if (presets is not null) throw presets;
        return warn;
    }

    public async Task<string?> SaveProgramsAsync(ProgramsConfig config)
    {
        await Host.SavePrograms(config);
        Programs = config;
        return Notify(ProgramsChanged);
    }
}
