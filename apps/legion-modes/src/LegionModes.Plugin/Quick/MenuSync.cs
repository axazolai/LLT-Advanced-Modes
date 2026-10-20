using LegionModes.Core;
using LegionModes.Core.Quick;

namespace LegionModes.Plugin.Quick;

// Keeps the desktop menu keys in HKCU equal to the menu for the current state: written only when the values differ from
// the ones written last. Never throws.
internal sealed class MenuSync
{
    readonly object _gate = new();
    IReadOnlyList<RegValue>? _last;

    public void Update(MenuState state)
    {
        try
        {
            var values = MenuRegistry.Values(DesktopMenu.Build(state), Paths.QuickExe, Paths.LltExe);
            lock (_gate)
            {
                if (_last is not null && _last.SequenceEqual(values)) return;
                MenuRegistry.Write(values);
                _last = values;
            }
        }
        catch (Exception e) { Log.Error("desktop menu sync", e); }
    }
}
