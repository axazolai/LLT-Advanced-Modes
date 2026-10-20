namespace LegionModes.Core.Engine;

// Delay and hold bookkeeping of one automatic profile (spec section 4, Profiles).
internal sealed class MatchClock
{
    DateTimeOffset? heldSince;

    public bool Matched { get; private set; }
    public DateTimeOffset? HoldUntil { get; private set; }
    public bool Holds { get; private set; }

    // When a delay is running (conditions hold, not yet matching): the instant it ends.
    public DateTimeOffset? PendingAt(int delaySec) =>
        Holds && !Matched && heldSince is { } s ? s.AddSeconds(delaySec) : null;

    // A source or lid change: every hold is dropped, every delay restarts from now.
    public void Reset(bool holds, DateTimeOffset now)
    {
        Matched = false;
        HoldUntil = null;
        Holds = holds;
        heldSince = holds ? now : null;
    }

    public void Step(bool holds, DateTimeOffset now, int delaySec, int holdSec)
    {
        Holds = holds;
        if (holds)
        {
            heldSince ??= now;
            if (Matched) HoldUntil = null;
            else if (now - heldSince.Value >= TimeSpan.FromSeconds(delaySec)) Matched = true;
            return;
        }
        heldSince = null;
        if (!Matched) return;
        HoldUntil ??= now.AddSeconds(holdSec);
        if (now >= HoldUntil)
        {
            Matched = false;
            HoldUntil = null;
        }
    }
}
