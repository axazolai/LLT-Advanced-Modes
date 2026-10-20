using LegionModes.Core.Model;
using LenovoLegionToolkit.Lib.Controllers;

namespace LegionModes.Plugin.Apply;

// Parameter 7, processor and Windows scheme: boost and EPP indexes of the scheme (AC and DC, read back), the scheme
// re-activated so they take effect, then the Windows power mode (overlay). DoNotTouch writes nothing.
internal static class ProcessorApplier
{
    static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    static readonly Guid ProcessorBoost = new("be337238-0d82-4146-a960-4f3749d470c7");
    static readonly Guid ProcessorEpp = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

    // scheme: the one stored in the control record; null = the active scheme. Boost: Enabled is Windows "Aggressive"
    // (index 2), Disabled is 0. EPP in percent. Throws when a write does not read back.
    public static string? Apply(Guid? scheme, ProcessorScheme p)
    {
        var target = scheme ?? WindowsPower.ActiveScheme();
        var wrote = false;
        if (p.Boost != BoostMode.DoNotTouch)
        {
            WindowsPower.Write(target, SubProcessor, ProcessorBoost, p.Boost == BoostMode.Enabled ? 2u : 0u, "processor boost");
            wrote = true;
        }
        if (p.Epp is { } epp)
        {
            WindowsPower.Write(target, SubProcessor, ProcessorEpp, (uint)Math.Clamp(epp, 0, 100), "energy/performance preference");
            wrote = true;
        }
        if (wrote) WindowsPower.Activate(target);
        if (p.PowerMode != Core.Model.WindowsPowerMode.DoNotTouch)
        {
            var mode = p.PowerMode switch
            {
                Core.Model.WindowsPowerMode.BestPowerEfficiency => LenovoLegionToolkit.Lib.WindowsPowerMode.BestPowerEfficiency,
                Core.Model.WindowsPowerMode.BestPerformance => LenovoLegionToolkit.Lib.WindowsPowerMode.BestPerformance,
                _ => LenovoLegionToolkit.Lib.WindowsPowerMode.Balanced,
            };
            WindowsPowerModeController.ApplyActiveOverlayScheme(WindowsPowerModeController.GuidForWindowsPowerMode(mode));
        }
        return null;
    }
}
