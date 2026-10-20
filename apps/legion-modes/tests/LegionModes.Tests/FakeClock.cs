using LegionModes.Core.Engine;

namespace LegionModes.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => Now += by;
}
