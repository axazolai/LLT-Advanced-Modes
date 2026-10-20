using LegionModes.Core.Engine;

namespace LegionModes.Core.Supervision;

public enum LltExitKind { Graceful, Crash }
public enum KeeperAction { Restart, Stay, GiveUp }

public sealed class KeeperPolicy
{
    private readonly IClock _clock;
    private readonly int _maxCrashes;
    private readonly TimeSpan _window;
    private readonly List<DateTimeOffset> _crashes = new();
    private bool _gaveUp;

    public KeeperPolicy(IClock clock, int maxCrashes = 3, TimeSpan? window = null)
    {
        _clock = clock;
        _maxCrashes = maxCrashes;
        _window = window ?? TimeSpan.FromMinutes(10);
    }

    private readonly object _lock = new();

    public KeeperAction OnLltExited(LltExitKind kind)
    {
        lock (_lock)
        {
            if (kind == LltExitKind.Graceful) return KeeperAction.Stay;
            if (_gaveUp) return KeeperAction.GiveUp;
            var now = _clock.Now;
            _crashes.RemoveAll(c => now - c > _window);
            _crashes.Add(now);
            if (_crashes.Count < _maxCrashes) return KeeperAction.Restart;
            _gaveUp = true;
            return KeeperAction.GiveUp;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _crashes.Clear();
            _gaveUp = false;
        }
    }
}
