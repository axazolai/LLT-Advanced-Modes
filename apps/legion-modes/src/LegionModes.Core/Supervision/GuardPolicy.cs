using LegionModes.Core.Ipc;

namespace LegionModes.Core.Supervision;

public sealed record GuardState(
    bool UserSessionActive, bool KeeperRunning,
    DateTimeOffset? KeeperMissingSince, DateTimeOffset? TaskRunAt,
    bool LidClosed, DateTimeOffset? LidClosedAt,
    Heartbeat? LastHeartbeat, DateTimeOffset? UpdateInProgressSince,
    DateTimeOffset? ResumedAt,
    DateTimeOffset? SleptForLidAt,
    DateTimeOffset? DirectStartAt = null);

public enum GuardAction { None, RunKeeperTask, StartKeeperDirect, SleepNow }

public static class GuardPolicy
{
    public static readonly TimeSpan DirectStartAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DirectStartRepeat = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SilentPlugin = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SilentPluginDuringUpdate = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan UpdateFlagLifetime = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<GuardAction> Decide(GuardState s, DateTimeOffset now)
    {
        var actions = new List<GuardAction>();
        var keeper = DecideKeeper(s, now);
        if (keeper != GuardAction.None) actions.Add(keeper);
        if (ShouldSleep(s, now)) actions.Add(GuardAction.SleepNow);
        return actions;
    }

    private static GuardAction DecideKeeper(GuardState s, DateTimeOffset now)
    {
        if (!s.UserSessionActive || s.KeeperRunning) return GuardAction.None;
        if (s.TaskRunAt is not { } taskRun || (s.ResumedAt is { } resumed && resumed > taskRun))
            return GuardAction.RunKeeperTask;
        if (s.DirectStartAt is { } direct && direct >= taskRun)
            return now >= direct + DirectStartRepeat ? GuardAction.RunKeeperTask : GuardAction.None;
        return now >= taskRun + DirectStartAfter ? GuardAction.StartKeeperDirect : GuardAction.None;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static bool ShouldSleep(GuardState s, DateTimeOffset now)
    {
        if (!s.LidClosed) return false;
        DateTimeOffset? start = s.LidClosedAt is { } a && s.ResumedAt is { } b
            ? (a > b ? a : b)
            : s.LidClosedAt ?? s.ResumedAt;
        if (start is not { } st) return false;
        var updating = s.UpdateInProgressSince is { } u && now - Min(u, now) < UpdateFlagLifetime;
        var threshold = updating ? SilentPluginDuringUpdate : SilentPlugin;
        var base_ = s.SleptForLidAt is { } slept && slept > st ? slept : st;
        var heartbeatAt = s.LastHeartbeat is { } hb ? Min(hb.At, now) : st;
        return now - heartbeatAt >= threshold && now - base_ >= threshold;
    }
}
