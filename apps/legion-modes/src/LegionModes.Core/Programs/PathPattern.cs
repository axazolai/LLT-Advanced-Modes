using System.Text.RegularExpressions;

namespace LegionModes.Core.Programs;

public static class PathPattern
{
    // expand %VARS%, '/' -> '\', trim trailing '\', upper-invariant
    public static string Normalize(string path)
    {
        var s = Environment.ExpandEnvironmentVariables(path).Replace('/', '\\');
        if (s.Length > 1) s = s.TrimEnd('\\');
        return s.ToUpperInvariant();
    }

    // '*' = within one segment, '**' = any depth; a pattern ending in '\' or '/' = everything below that folder
    public static bool IsMatch(string pattern, string fullPath)
    {
        if (pattern.EndsWith('\\') || pattern.EndsWith('/')) pattern += "**";
        var p = Normalize(pattern); var f = Normalize(fullPath);
        if (!p.Contains('\\')) return string.Equals(Path.GetFileName(f), p, StringComparison.Ordinal);
        var rx = "^" + Regex.Escape(p).Replace(@"\*\*", "\u0001").Replace(@"\*", @"[^\\]*").Replace("\u0001", ".*") + "$";
        return Regex.IsMatch(f, rx, RegexOptions.CultureInvariant);
    }
}
