using LegionModes.Core.Model;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;

namespace LegionModes.Plugin.Backlight;

internal static class LightingApplier
{
    // null = ok or skipped (keyboard not supported).
    public static async Task<string?> ApplyAsync(Lighting l)
    {
        var controller = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();
        if (!await controller.IsSupportedAsync().ConfigureAwait(false))
        {
            Log.Trace("lighting skipped: Spectrum keyboard backlight is not supported on this machine");
            return null;
        }
        await controller.SetProfileAsync(l.Profile).ConfigureAwait(false);
        await controller.SetBrightnessAsync(l.Brightness).ConfigureAwait(false);
        return null;
    }
}
