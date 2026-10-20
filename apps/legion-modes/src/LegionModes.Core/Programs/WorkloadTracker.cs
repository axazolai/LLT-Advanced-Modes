using LegionModes.Core.Engine;
using LegionModes.Core.Model;

namespace LegionModes.Core.Programs;

// Thread-safe. Levels Work..Heavy (1..3); Ignore (0) never counts. Level L is held while a process of class >= L runs, or until lowerDelay after the last one stopped.
public sealed class WorkloadTracker
{
    readonly object _gate = new();
    readonly IClock _clock;
    readonly TimeSpan _lowerDelay;
    readonly Dictionary<int, ProgramClass> _running = new();
    readonly bool[] _held = new bool[4];
    readonly DateTimeOffset?[] _stoppedAt = new DateTimeOffset?[4];

    public WorkloadTracker(IClock clock, TimeSpan lowerDelay) { _clock = clock; _lowerDelay = lowerDelay; }

    public void Started(int pid, ProgramClass c) { lock (_gate) { Evaluate(); _running[pid] = c; Evaluate(); } }

    public void Stopped(int pid) { lock (_gate) { Evaluate(); _running.Remove(pid); Evaluate(); } }

    public void Resync(IReadOnlyDictionary<int, ProgramClass> running)
    {
        lock (_gate)
        {
            Evaluate();
            _running.Clear();
            foreach (var (pid, c) in running) _running[pid] = c;
            Evaluate();
        }
    }

    public Workload Current
    {
        get
        {
            lock (_gate)
            {
                Evaluate();
                return _held[3] ? Workload.Heavy : _held[2] ? Workload.Ai : _held[1] ? Workload.Work : Workload.Idle;
            }
        }
    }

    public DateTimeOffset? NextChangeAt
    {
        get
        {
            lock (_gate)
            {
                Evaluate();
                DateTimeOffset? next = null;
                for (var l = 1; l <= 3; l++)
                    if (_held[l] && _stoppedAt[l] is { } s && (next is null || s + _lowerDelay < next)) next = s + _lowerDelay;
                return next;
            }
        }
    }

    void Evaluate()
    {
        var now = _clock.Now;
        for (var l = 1; l <= 3; l++)
        {
            if (_running.Values.Any(c => (int)c >= l)) { _held[l] = true; _stoppedAt[l] = null; }
            else if (_held[l])
            {
                _stoppedAt[l] ??= now;
                if (now >= _stoppedAt[l] + _lowerDelay) { _held[l] = false; _stoppedAt[l] = null; }
            }
        }
    }
}
