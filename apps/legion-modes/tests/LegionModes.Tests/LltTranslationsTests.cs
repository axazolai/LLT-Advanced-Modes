using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LegionModes.Tests;

public class LltTranslationsTests
{
    static readonly Dictionary<string, string> Classes = new()
    {
        ["LenovoLegionToolkit.Lib"] = "LenovoLegionToolkit.Lib.Resources.Resource",
        ["LenovoLegionToolkit.Lib.Automation"] = "LenovoLegionToolkit.Lib.Automation.Resources.Resource",
        ["Lenovo Legion Toolkit"] = "LenovoLegionToolkit.WPF.Resources.Resource",
    };

    // A corrected text that names a key LLT lacks does nothing; one whose {n} placeholders differ from LLT's English
    // makes LLT's string.Format throw inside LLT.
    [Fact(DisplayName = "@critical every corrected LLT text names a key of the pinned LLT and keeps its English placeholders")]
    public void CorrectionsMatchLltResources()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LegionModes.sln"))) d = d.Parent;
        var app = d!.FullName;
        using var lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "llt.lock.json")));
        var version = lockFile.RootElement.GetProperty("llt").GetProperty("version").GetString()!;
        var host = Path.Combine(app, "..", "..", "_work", "llt-ref", version, "{app}");
        var tables = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(Path.Combine(app, "src", "LegionModes.Plugin", "Ui", "llt-ru.json")))!;

        var problems = new List<string>();
        foreach (var (assembly, fixes) in tables)
        {
            Assert.True(Classes.ContainsKey(assembly), "unknown assembly " + assembly);
            var resources = new ResourceManager(Classes[assembly], Assembly.LoadFrom(Path.Combine(host, assembly + ".dll")));
            foreach (var (key, ru) in fixes)
            {
                var en = resources.GetString(key, CultureInfo.InvariantCulture);
                if (en is null) { problems.Add($"{assembly}/{key}: no such key"); continue; }
                if (string.IsNullOrWhiteSpace(ru)) problems.Add($"{assembly}/{key}: empty");
                if (!Placeholders(en).SetEquals(Placeholders(ru))) problems.Add($"{assembly}/{key}: placeholders «{en}» vs «{ru}»");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Equal("Вкл.", tables["LenovoLegionToolkit.Lib"]["ToggleState_On"]);
    }

    static HashSet<string> Placeholders(string s) =>
        Regex.Matches(s, @"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}").Select(m => m.Groups[1].Value).ToHashSet();
}
