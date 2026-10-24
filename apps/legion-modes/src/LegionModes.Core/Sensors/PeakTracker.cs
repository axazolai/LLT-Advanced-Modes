using LegionModes.Core.Engine;

namespace LegionModes.Core.Sensors;

// The highest value since the session started; once the session is older than 24 hours, the highest value of the last 24 hours.
// One bucket per minute (at most 1440). Not stored on disk: a new keeper starts empty.
public sealed class PeakTracker
{
    const int Minutes = 24 * 60;
    readonly IClock _clock;
    readonly DateTimeOffset _start;
    readonly List<(long Minute, int Max)> _buckets = new();

    public PeakTracker(IClock clock)
    {
        _clock = clock;
        _start = clock.Now;
    }

    // The session is at least 24 hours old: Peak is then the maximum of the last 24 hours.
    public bool OverDay => _clock.Now - _start >= TimeSpan.FromHours(24);

    public int? Peak
    {
        get
        {
            Prune();
            return _buckets.Count == 0 ? null : _buckets.Max(b => b.Max);
        }
    }

    public void Add(int c)
    {
        var minute = Minute();
        // The same minute, or the clock went back: merge into the newest bucket.
        if (_buckets.Count > 0 && minute <= _buckets[^1].Minute)
            _buckets[^1] = (_buckets[^1].Minute, Math.Max(_buckets[^1].Max, c));
        else
            _buckets.Add((minute, c));
        Prune();
    }

    long Minute() => _clock.Now.ToUnixTimeSeconds() / 60;

    void Prune()
    {
        // A bucket in the future (the clock jumped forward and came back) keeps its value but takes the current minute, so it ages out in 24 hours.
        var now = Minute();
        if (_buckets.Any(b => b.Minute > now))
        {
            var future = _buckets.Where(b => b.Minute > now).Max(b => b.Max);
            _buckets.RemoveAll(b => b.Minute > now);
            if (_buckets.Count > 0 && _buckets[^1].Minute == now) _buckets[^1] = (now, Math.Max(_buckets[^1].Max, future));
            else _buckets.Add((now, future));
        }
        var floor = now - (Minutes - 1);
        _buckets.RemoveAll(b => b.Minute < floor);
    }
}
