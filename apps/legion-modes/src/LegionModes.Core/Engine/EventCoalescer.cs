namespace LegionModes.Core.Engine;

public sealed class EventCoalescer(IClock clock, TimeSpan window)
{
    DateTimeOffset? first;
    bool full;

    public void Signal(bool isFull)
    {
        first ??= clock.Now;
        full |= isFull;
    }

    // true once the window has passed since the first signal of a burst; the burst is then reset
    public bool TryTake(out bool isFull)
    {
        isFull = false;
        if (first is not { } t || clock.Now - t < window) return false;
        isFull = full;
        first = null;
        full = false;
        return true;
    }
}
