using System.Windows;
using LegionModes.Core;
using LegionModes.Plugin.Apply;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows.Dashboard;

namespace LegionModes.Plugin.Ui;

// LLT's GodMode window on a profile's own preset `AM · <name>`: the runtime holds its applies, the active preset and the
// power mode are captured, the profile's preset is made active, LLT's window opens (its Save writes and applies the
// preset), and the captured preset and mode come back when it closes.
internal static class PresetEditor
{
    public static async Task EditAsync(Window owner, IModesHost host, LegionModes.Core.Model.Profile profile)
    {
        host.BeginGodModeEdit();
        try
        {
            var capture = await GodModePresets.CaptureAsync();
            try
            {
                await GodModePresets.ActivateAsync(profile);
                new GodModeSettingsWindow { Owner = owner }.ShowDialog();
            }
            finally { await GodModePresets.RestoreAsync(capture); }
        }
        catch (Exception e)
        {
            Log.Error("preset edit", e);
            await MessageBoxHelper.ShowAsync(owner, Tr.T("Preset not opened"), e.Message, "OK", "");
        }
        finally { host.EndGodModeEdit(); }
    }
}
