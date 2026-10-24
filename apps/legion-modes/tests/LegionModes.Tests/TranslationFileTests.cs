using System.Text.Json;
using System.Text.RegularExpressions;
using LegionModes.Core;
using LegionModes.Core.Model;
using Xunit;

namespace LegionModes.Tests;

// The translation files against the sources. Keys are English: every Tr.T / Tr.F key used in the code is in en.json and in
// ru.json with the same placeholders; the two files hold the same keys; a key is never Russian.
public sealed partial class TranslationFileTests
{
    // Tr.T( / Tr.F( then a string literal, then "+ literal" pieces: the key is their concatenation.
    [GeneratedRegex("""Tr\.(?:T|F)\(\s*(?<l>"(?:[^"\\]|\\.)*"(?:\s*\+\s*"(?:[^"\\]|\\.)*")*)""")]
    private static partial Regex Call();

    [GeneratedRegex("""
        "((?:[^"\\]|\\.)*)"
        """)]
    private static partial Regex Piece();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[А-Яа-яЁё]")]
    private static partial Regex Cyrillic();

    static string LangDir => TestLang.LangDir;

    static string AppRoot() => Path.GetFullPath(Path.Combine(LangDir, "..", "..", ".."));

    static Dictionary<string, string> Load(string name) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(LangDir, name + ".json")))!;

    static IEnumerable<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);

    static IEnumerable<(string File, string Key)> KeysInCode()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.EndsWith("Tr.cs")))
            foreach (Match m in Call().Matches(File.ReadAllText(file)))
                yield return (Path.GetFileName(file), string.Concat(Piece().Matches(m.Groups["l"].Value).Select(p => Regex.Unescape(p.Groups[1].Value))));
    }

    [Fact(DisplayName = "@important every Tr key in the code is English and has a text in en.json and ru.json with the same placeholders")]
    public void EveryKeyTranslated()
    {
        var en = Load("en");
        var ru = Load("ru");
        var problems = new List<string>();
        var keys = KeysInCode().ToList();
        Assert.True(keys.Count > 400, "the scan found " + keys.Count + " keys: the pattern is broken");
        foreach (var (file, key) in keys)
        {
            if (Cyrillic().IsMatch(key)) problems.Add($"{file}: the key is not English: \"{key}\"");
            foreach (var (name, map) in new[] { ("en", en), ("ru", ru) })
            {
                if (!map.TryGetValue(key, out var text) || text.Length == 0) problems.Add($"{file}: no {name} text for \"{key}\"");
                else if (!Placeholders(key).SequenceEqual(Placeholders(text))) problems.Add($"{file}: placeholders differ in {name} for \"{key}\"");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct().Take(40)));
    }

    [Fact(DisplayName = "@important en.json and ru.json hold the same keys; English texts are the keys, Russian texts are Russian")]
    public void FilesAgree()
    {
        var en = Load("en");
        var ru = Load("ru");
        Assert.True(en.Keys.ToHashSet().SetEquals(ru.Keys), "en.json and ru.json differ in keys");
        var wrong = en.Where(kv => Regex.Replace(kv.Key, @"\|\d+$", "") != kv.Value).Select(kv => kv.Key).Take(10).ToList();
        Assert.True(wrong.Count == 0, "en.json maps a key to other text: " + string.Join("; ", wrong));
        var notRussian = ru.Where(kv => !Cyrillic().IsMatch(kv.Value) && kv.Value != kv.Key).Select(kv => kv.Key).Take(40).ToList();
        Assert.True(notRussian.Count < 30, "ru.json holds non-Russian texts for: " + string.Join("; ", notRussian));
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
