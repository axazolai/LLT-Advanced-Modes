namespace LegionModes.Core.Programs;

public enum ProgramClass { Ignore = 0, Work = 1, Ai = 2, Heavy = 3 }          // numeric = Workload value; Ignore counts as idle

public sealed record ProgramEntry(string Pattern, ProgramClass Class, string? CommandLine = null, bool DenyWakeRequests = false);

public sealed class ProgramClassifier
{
    readonly List<ProgramEntry> _entries;

    public ProgramClassifier(IEnumerable<ProgramEntry> entries) => _entries = entries.ToList();

    // Highest class among matching entries (Ignore only when nothing higher matches). An entry with CommandLine matches
    // only a non-null matching command line. exePath null -> only bare-name entries, matched against processName
    // (+ ".exe" when it has no extension).
    public ProgramClass? Classify(string? exePath, string processName, string? commandLine)
    {
        var name = Path.HasExtension(processName) ? processName : processName + ".exe";
        ProgramClass? best = null;
        foreach (var e in _entries)
        {
            var bare = !e.Pattern.Contains('\\') && !e.Pattern.Contains('/');
            bool hit;
            if (exePath is null) hit = bare && PathPattern.IsMatch(e.Pattern, name);
            else hit = PathPattern.IsMatch(e.Pattern, exePath);
            if (hit && e.CommandLine is { } cl) hit = commandLine is not null && CommandLinePattern.IsMatch(cl, commandLine);
            if (hit && (best is null || e.Class > best)) best = e.Class;
        }
        return best;
    }
}
