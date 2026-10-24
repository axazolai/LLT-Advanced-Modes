using System.Collections.ObjectModel;
using LegionModes.Core;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers.GodMode;
using LenovoLegionToolkit.Lib.Features;

namespace LegionModes.Plugin.Apply;

public static class CurveOptimizerReset
{
    // Every GodMode preset whose name starts with "AM · " gets the all-core Curve Optimizer at 0 and switched off, in one
    // state write; the machine is re-applied when GodMode is the active power mode. No other preset is touched.
    public static async Task RunAsync()
    {
        await GodModePresets.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var gm = IoCContainer.Resolve<GodModeController>();
            var state = await gm.GetStateAsync().ConfigureAwait(false);
            var template = state.Presets.TryGetValue(state.ActivePresetId, out var active) ? active.AllCoreCurveOptimizer : null;
            var presets = new Dictionary<Guid, GodModePreset>(state.Presets);
            foreach (var (id, preset) in state.Presets)
            {
                if (!Paths.IsOurs(preset.Name)) continue;
                presets[id] = preset with
                {
                    AllCoreCurveOptimizer = (preset.AllCoreCurveOptimizer ?? template)?.WithValue(0),
                    EnableAllCoreCurveOptimizer = false,
                };
            }
            await gm.SetStateAsync(state with { Presets = new ReadOnlyDictionary<Guid, GodModePreset>(presets) }).ConfigureAwait(false);

            if (await IoCContainer.Resolve<PowerModeFeature>().GetStateAsync().ConfigureAwait(false) == PowerModeState.GodMode)
            {
                var (applied, _) = await gm.ApplyStateWithResultAsync().ConfigureAwait(false);
                if (!applied) throw new InvalidOperationException(Tr.T("GodMode not applied: Vantage / Legion Space / Legion Zone are on"));
            }
        }
        finally { GodModePresets.Gate.Release(); }
    }
}
