namespace LegionModes.Core.Engine;

public enum DisplayStateKind { Off = 0, On = 1, Dimmed = 2 }   // values of GUID_CONSOLE_DISPLAY_STATE
public enum LightingAction { None, SwitchOff, Restore }

public static class IdleLightingPolicy
{
    // active = the plugin has control and is not paused. Dimmed or Off -> SwitchOff once (repeats -> None);
    // On after a SwitchOff -> Restore; On otherwise -> None; not active -> None, and a later On restores nothing.
    public static LightingAction Next(DisplayStateKind previous, DisplayStateKind now, bool active, bool switchedOff)
    {
        if (!active) return LightingAction.None;
        if (now == DisplayStateKind.On) return switchedOff ? LightingAction.Restore : LightingAction.None;
        return switchedOff ? LightingAction.None : LightingAction.SwitchOff;
    }
}
