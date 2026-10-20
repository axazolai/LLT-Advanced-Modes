namespace LegionModes.Core.Status;

public static class ShutdownSafety
{
    public const int Threshold = 3;

    // At plugin start: previous run not marked clean -> count + 1; clean -> 0. At 3 -> reset Curve Optimizer, count 0.
    public static (int Count, bool ResetCurveOptimizer) OnStart(int abnormalShutdowns, bool previousRunClean)
    {
        if (previousRunClean) return (0, false);
        var count = Math.Max(0, abnormalShutdowns) + 1;
        return count >= Threshold ? (0, true) : (count, false);
    }
}
