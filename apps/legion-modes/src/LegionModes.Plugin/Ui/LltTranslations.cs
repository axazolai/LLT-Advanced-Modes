using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.Json;

namespace LegionModes.Plugin.Ui;

// Corrections to LLT 2.36.4.0's Russian resources (owner 2026-10-20: a full correction — mistranslations, strings left in
// English, wording that does not read plainly; events «Когда …», states «Вкл.» / «Выкл.»). The texts are in llt-ru.json
// (embedded): LLT assembly -> resource key -> Russian text. Each LLT resource class gets a ResourceManager that answers
// those keys for Russian and passes everything else to LLT's own (its private static `resourceMan`, a reflection
// contract).
public static class LltTranslations
{
    // The resource class of each LLT assembly in llt-ru.json.
    static readonly Dictionary<string, string> Classes = new()
    {
        ["LenovoLegionToolkit.Lib"] = "LenovoLegionToolkit.Lib.Resources.Resource",
        ["LenovoLegionToolkit.Lib.Automation"] = "LenovoLegionToolkit.Lib.Automation.Resources.Resource",
        ["Lenovo Legion Toolkit"] = "LenovoLegionToolkit.WPF.Resources.Resource",
    };

    public static IReadOnlyDictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(LltTranslations).Assembly.GetManifestResourceStream("LegionModes.Plugin.Ui.llt-ru.json")
                           ?? throw new InvalidOperationException("llt-ru.json is not embedded");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
    }

    // Once, at plugin start. A class whose field is missing is skipped and named in the result (the compatibility check
    // reports the same before an update).
    // What the last Apply could not correct (shown on Status).
    public static IReadOnlyList<string> Problems { get; private set; } = [];

    public static IReadOnlyList<string> Apply()
    {
        var problems = new List<string>();
        Problems = problems;
        IReadOnlyDictionary<string, Dictionary<string, string>> tables;
        try { tables = Load(); }
        catch (Exception e)
        {
            Log.Error("llt translations", e);
            problems.Add(e.Message);
            return problems;
        }
        foreach (var (assembly, fixes) in tables)
        {
            try
            {
                if (!Classes.TryGetValue(assembly, out var className)) throw new InvalidOperationException("unknown assembly " + assembly);
                var type = Type.GetType($"{className}, {assembly}", throwOnError: true)!;
                var field = type.GetField("resourceMan", BindingFlags.NonPublic | BindingFlags.Static)
                            ?? throw new MissingFieldException(type.FullName, "resourceMan");
                var original = (ResourceManager)type.GetProperty("ResourceManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                if (original is Corrected) continue;
                field.SetValue(null, new Corrected(original, fixes));
            }
            catch (Exception e)
            {
                problems.Add($"{assembly}: {e.Message}");
                Log.Error("llt translations " + assembly, e);
            }
        }
        return problems;
    }

    // Answers the fixed keys for Russian; everything else goes to LLT's own manager.
    sealed class Corrected(ResourceManager original, IReadOnlyDictionary<string, string> fixes) : ResourceManager
    {
        public override string BaseName => original.BaseName;

        public override string? GetString(string name, CultureInfo? culture) =>
            (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "ru" && fixes.TryGetValue(name, out var fix)
                ? fix
                : original.GetString(name, culture);

        public override string? GetString(string name) => GetString(name, null);

        public override object? GetObject(string name, CultureInfo? culture) => original.GetObject(name, culture);

        public override object? GetObject(string name) => original.GetObject(name);

        public override ResourceSet? GetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents) =>
            original.GetResourceSet(culture, createIfNotExists, tryParents);
    }
}
