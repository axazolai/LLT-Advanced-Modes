using System.Text.Json;
using System.Text.RegularExpressions;
using LegionModes.Core;
using LegionModes.Core.Model;
using Xunit;

namespace LegionModes.Tests;

// lang/en.json against the sources: every Tr.T / Tr.F key used in the code has an English text with the same placeholders,
// and every factory profile name has one (names are shown through Tr.T(profile.Name)).
public sealed partial class TranslationFileTests
{
    [GeneratedRegex("""Tr\.(?:T|F)\(\s*"((?:[^"\\]|\\.)*)"(?<f>\s*[,)])""")]
    private static partial Regex Call();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "LegionModes.Core"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("apps/legion-modes not found above " + AppContext.BaseDirectory);
    }

    static Dictionary<string, string> English() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppRoot(), "src", "LegionModes.Plugin", "lang", "en.json")))!;

    static IEnumerable<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);

    [Fact(DisplayName = "@important en.json holds an English text for every Tr key in the code, with the same placeholders")]
    public void EveryKeyTranslated()
    {
        var en = English();
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.EndsWith("Tr.cs")))
        {
            foreach (Match m in Call().Matches(File.ReadAllText(file)))
            {
                var key = Regex.Unescape(m.Groups[1].Value);
                if (!en.TryGetValue(key, out var text) || text.Length == 0) problems.Add($"{Path.GetFileName(file)}: no English text for \"{key}\"");
                else if (!Placeholders(key).SequenceEqual(Placeholders(text))) problems.Add($"{Path.GetFileName(file)}: placeholders differ in \"{key}\"");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct().Take(40)));
    }

    [Fact(DisplayName = "@important every built-in profile carries Russian and English names")]
    public void FactoryNamesTranslated()
    {
        var missing = FactoryProfiles.Create().Profiles
            .Where(p => p.Names is null || !p.Names.TryGetValue("en", out var en) || en.Length == 0 || !p.Names.TryGetValue("ru", out var ru) || ru != p.Name)
            .Select(p => p.Name).ToList();
        Assert.True(missing.Count == 0, "names missing for: " + string.Join("; ", missing));
    }
}
