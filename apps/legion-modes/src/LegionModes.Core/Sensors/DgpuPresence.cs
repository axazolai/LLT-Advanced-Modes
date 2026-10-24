using LegionModes.Core.Engine;

namespace LegionModes.Core.Sensors;

// Whether the dGPU is awake, from the controller's dGPU temperature (0 = asleep). Awake after two samples above zero in a row;
// asleep after zero samples for 10 seconds on end (a short dip does not hide the icon).
public sealed class DgpuPresence(IClock clock)
{
    static readonly TimeSpan AsleepAfter = TimeSpan.FromSeconds(10);
    int _positives;
    DateTimeOffset? _zeroSince;

    public bool Awake { get; private set; }

    // c null = the controller was not asked or the value is unusable: nothing changes.
    public bool Update(int? c)
    {
        if (c is null) return Awake;
        if (c > 0)
        {
            _zeroSince = null;
            if (++_positives >= 2) Awake = true;
        }
        else
        {
            _positives = 0;
            _zeroSince ??= clock.Now;
            if (Awake && clock.Now - _zeroSince >= AsleepAfter) Awake = false;
        }
        return Awake;
    }
}
