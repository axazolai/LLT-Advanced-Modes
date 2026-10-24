using LegionModes.Core.Model;

namespace LegionModes.Core.Engine;

public sealed record WindowsTimerValues(uint DimSec, uint DimmedBrightness, uint VideoIdleSec, uint StandbyIdleSec,
    uint HibernateIdleSec, int? StaticBrightness);

public static class WindowsTimers
{
    // The profile's own terms for values read back from a scheme (StaticBrightness is not part of a scheme).
    public static IdleTimers ToIdle(WindowsTimerValues v)
    {
        var sleepMin = (int)(v.StandbyIdleSec / 60);
        var hibernateMin = v.HibernateIdleSec == 0 ? 0 : sleepMin > 0 ? (int)(v.HibernateIdleSec / 60) - sleepMin : (int)(v.HibernateIdleSec / 60);
        return new IdleTimers(v.DimSec > 0 ? new DimStage((int)(v.DimSec / 60), (int)v.DimmedBrightness) : null,
            (int)(v.VideoIdleSec / 60), sleepMin, Math.Max(hibernateMin, 0), null);
    }

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
