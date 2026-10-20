using LegionModes.Core.Model;

namespace LegionModes.Core.Engine;

// Which failed parameters are applied again. A parameter that failed is retried at most `maxRetries` times, `period` apart,
// for the same effective value; after that it is left until the value changes.
public sealed class RetryTracker(TimeSpan period, int maxRetries = 3)
{
    sealed record Entry(object? Value, int Retries, DateTimeOffset At);

    readonly Dictionary<ParameterId, Entry> _entries = [];

    // A parameter's result: failed = it must be tried again. `value` is the effective value that was applied.
    public void Record(ParameterId id, object? value, bool failed, DateTimeOffset now)
    {
        if (!failed) { _entries.Remove(id); return; }
        var retries = _entries.TryGetValue(id, out var e) && Equals(e.Value, value) ? e.Retries + 1 : 0;
        _entries[id] = new Entry(value, retries, now + period);
    }

    // The parameters whose retry is due and whose value is still the one that failed.
    public IReadOnlyList<ParameterId> Due(DateTimeOffset now, Func<ParameterId, object?> current)
    {
        var due = new List<ParameterId>();
        foreach (var (id, e) in _entries.ToArray())
        {
            if (!Equals(e.Value, current(id))) { _entries.Remove(id); continue; }   // the value moved on: the normal diff applies it
            if (e.Retries < maxRetries && now >= e.At) due.Add(id);
        }
        return due;
    }

    public void Forget(ParameterId id) => _entries.Remove(id);
    public void Clear() => _entries.Clear();
}
