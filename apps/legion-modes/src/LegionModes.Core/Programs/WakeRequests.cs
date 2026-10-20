using System.Text.RegularExpressions;

namespace LegionModes.Core.Programs;

// Kind: DISPLAY / SYSTEM / AWAYMODE / EXECUTION / PERFBOOST / ACTIVELOCKSCREEN; Caller: PROCESS / SERVICE / DRIVER
public sealed record WakeRequest(string Kind, string Caller, string Name, string? Detail);

public static class WakeRequests
{
    static readonly Regex Header = new(@"^([A-Za-z]+):$", RegexOptions.CultureInvariant);
    static readonly Regex Request = new(@"^\[([A-Za-z]+)\]\s*(.*)$", RegexOptions.CultureInvariant);
    static readonly Regex TrailingParens = new(@"\s*\([^()]*\)$", RegexOptions.CultureInvariant);

    // Parses `powercfg /requests`. Name: the file name for a PROCESS (the tool prints a device path), the text without a
    // trailing "(instance id)" otherwise.
    public static IReadOnlyList<WakeRequest> Parse(string text)
    {
        var result = new List<WakeRequest>();
        string? kind = null, caller = null, name = null;
        var open = false;           // a request waits for its detail line

        void Flush(string? detail)
        {
            if (open && kind is not null) result.Add(new(kind, caller!, name!, detail));
            open = false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(null); continue; }
            if (Header.Match(line) is { Success: true } h) { Flush(null); kind = h.Groups[1].Value.ToUpperInvariant(); continue; }
            if (Request.Match(line) is { Success: true } r)
            {
                Flush(null);
                if (kind is null) continue;
                caller = r.Groups[1].Value.ToUpperInvariant();
                var n = r.Groups[2].Value.Trim();
                name = caller == "PROCESS" ? n.Substring(n.LastIndexOfAny(['\\', '/']) + 1) : TrailingParens.Replace(n, "");
                open = true;
                continue;
            }
            if (open) Flush(line);   // the detail; a line with no open request ("None.", "Нет.") is ignored
        }
        Flush(null);
        return result;
    }
}
