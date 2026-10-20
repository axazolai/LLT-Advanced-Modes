namespace LegionModes.Updates;

public static class ChangelogHighlighter
{
    public static readonly string[] Keywords = ["godmode", "god mode", "fan", "power", "plugin", "extension", "station",
        "automation", "tray", "dashboard", "spectrum", "hybrid", "battery", "refresh"];

    public static IReadOnlyList<(string Line, bool Highlight)> Highlight(string releaseBody)
    {
        var result = new List<(string, bool)>();
        foreach (var raw in (releaseBody ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            result.Add((line, Keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase))));
        }
        return result;
    }
}
