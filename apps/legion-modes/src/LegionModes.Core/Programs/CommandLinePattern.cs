using System.Text.RegularExpressions;

namespace LegionModes.Core.Programs;

public static class CommandLinePattern
{
    // '*' = any run of characters, case-insensitive, matched against the whole command line
    public static bool IsMatch(string pattern, string commandLine)
    {
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
        return Regex.IsMatch(commandLine, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}
