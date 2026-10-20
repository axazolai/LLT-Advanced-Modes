namespace LegionModes.Core.Programs;

public static class WakeOverrides
{
    // exe file name of an entry's pattern, or null when the pattern has none (no '.exe' name, or a wildcard in it)
    internal static string? ExeName(string pattern)
    {
        var name = pattern.Substring(pattern.LastIndexOfAny(['\\', '/']) + 1);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Length > 4 && !name.Contains('*') ? name : null;
    }

    public static IReadOnlyList<string> ExeNames(ProgramsConfig c) =>
        c.Entries.Where(e => e is { DenyWakeRequests: true }).Select(e => ExeName(e.Pattern))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
