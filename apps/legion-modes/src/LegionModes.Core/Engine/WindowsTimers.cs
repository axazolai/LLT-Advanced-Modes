using LegionModes.Core.Model;

namespace LegionModes.Core.Engine;

public sealed record WindowsTimerValues(uint DimSec, uint DimmedBrightness, uint VideoIdleSec, uint StandbyIdleSec,
    uint HibernateIdleSec, int? StaticBrightness);

public static class WindowsTimers
{
    public static WindowsTimerValues From(IdleTimers t)
    {
        var dynamic = t.Dim is { Minutes: > 0 };
        var hibernateMin = t.HibernateMin == 0 ? 0 : t.SleepMin > 0 ? t.SleepMin + t.HibernateMin : t.HibernateMin;
        return new WindowsTimerValues(
            DimSec: dynamic ? (uint)(t.Dim!.Minutes * 60) : 0,
            DimmedBrightness: (uint)(t.Dim?.Brightness ?? 50),
            VideoIdleSec: (uint)(t.DisplayOffMin * 60),
            StandbyIdleSec: (uint)(t.SleepMin * 60),
            HibernateIdleSec: (uint)(hibernateMin * 60),
            StaticBrightness: t.Dim is { Minutes: 0 } d ? d.Brightness : null);
    }
}
