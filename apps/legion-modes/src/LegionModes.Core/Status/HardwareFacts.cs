using LegionModes.Core.Model;

namespace LegionModes.Core.Status;

public enum ChargeKind { Conservation, Normal, Rapid }

public enum UsbKind { Off, WhenSleeping, Always }

// Whether a state read from the hardware is the one a profile asked for (the Status tab's «Фактически»). "Do not touch" always matches.
public static class HardwareFacts
{
    // A fixed rate: the signal at that rate (within 1 Hz) and no DRR; DRR: the DRR flag of the display path.
    public static bool PanelMatches(PanelRate asked, int physicalHz, bool drr) => asked.Kind switch
    {
        PanelRateKind.DoNotTouch => true,
        PanelRateKind.Fixed => !drr && Math.Abs(physicalHz - asked.Hz) <= 1,
        _ => drr,
    };

    public static bool UsbMatches(UsbPower asked, UsbKind actual) => asked switch
    {
        UsbPower.Always => actual == UsbKind.Always,
        UsbPower.WhenSleeping => actual == UsbKind.WhenSleeping,
        _ => actual == UsbKind.Off,
    };

    // The projection the display paths show against the one asked; "do not touch" matches anything.
    public static bool ProjectionMatches(Projection asked, Projection actual) => asked == Projection.DoNotTouch || asked == actual;

    // Boost: the Windows index of the active scheme (2 = aggressive, 0 = disabled); EPP in percent; the Windows power mode of the overlay.
    // A field the profile does not touch (or that cannot be read) is not compared.
    public static bool ProcessorMatches(ProcessorScheme asked, uint? boost, uint? epp, WindowsPowerMode? overlay)
    {
        if (asked.Boost != BoostMode.DoNotTouch && boost is { } b && b != (asked.Boost == BoostMode.Enabled ? 2u : 0u)) return false;
        if (asked.Epp is { } e && epp is { } p && p != (uint)Math.Clamp(e, 0, 100)) return false;
        if (asked.PowerMode != WindowsPowerMode.DoNotTouch && overlay is { } o && o != asked.PowerMode) return false;
        return true;
    }

    // The four stages of the scheme against what the profile asked for (the static brightness is not a scheme value). Dimmed brightness only
    // counts while a dim stage is on.
    public static bool TimersMatch(LegionModes.Core.Engine.WindowsTimerValues asked, LegionModes.Core.Engine.WindowsTimerValues actual) =>
        asked.DimSec == actual.DimSec && (asked.DimSec == 0 || asked.DimmedBrightness == actual.DimmedBrightness)
        && asked.VideoIdleSec == actual.VideoIdleSec && asked.StandbyIdleSec == actual.StandbyIdleSec && asked.HibernateIdleSec == actual.HibernateIdleSec;

    // The keyboard's profile slot and brightness against the profile's. Brightness 0 is also what the idle lighting leaves, so it never
    // counts as a difference; a profile asking for 0 matches only 0.
    public static bool LightingMatches(Lighting asked, int profile, int brightness) =>
        asked.Profile == profile && (asked.Brightness == brightness || (brightness == 0 && asked.Brightness > 0));

    public static bool ChargeMatches(ChargingMode asked, ChargeKind actual) => asked switch
    {
        ChargingMode.DoNotTouch => true,
        ChargingMode.Conservation => actual == ChargeKind.Conservation,
        ChargingMode.Rapid => actual == ChargeKind.Rapid,
        _ => actual == ChargeKind.Normal,
    };
}
