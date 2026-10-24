using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LegionModes.Core;

// The owner-facing text. The English text is the key: Tr.T("Profiles") returns it as is, unless a translation file of the
// interface language holds another text for it. A translation file is named after the culture code (ru.json, de.json,
// de-DE.json): a flat JSON object {"English key": "text"}. Two folders: the package's (<plugin dir>\lang, replaced by every
// update) and the owner's own (<LLT data>\Plugins\Configs\LegionModes\lang, kept by every update); the owner's is laid over
// the package's. For a code the language part is laid over first, then the full code. A key found nowhere shows the key.
// A key may end in a context |2, |3 ... that tells two meanings of one English text apart; it is not shown.
public static partial class Tr
{
    static IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>();

    // The language code in use: "en", "ru", "de-DE" ...
    public static string Language { get; private set; } = "en";

    public static string UserLangDir { get; } = Path.Combine(Paths.LltDataDir, "Plugins", "Configs", Paths.PluginId, "lang");

    public static string PackageLangDir { get; } = Path.Combine(Path.GetDirectoryName(Paths.PluginDll)!, "lang");

    [GeneratedRegex(@"\|\d+$")]
    private static partial Regex Context();

    public static string T(string key) => _map.TryGetValue(key, out var t) && t.Length > 0 ? t : Context().Replace(key, "");

    // The text carries {0}, {1}... placeholders; the translation keeps the same ones.
    public static string F(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(key), args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, Context().Replace(key, ""), args); }   // a broken translation: the English key
    }

    // Raised (on the calling thread) when Use changed the language or the texts.
    public static event Action? Changed;

    // A culture code ("ru", "ru-RU", "en", "de-DE", ...); null or empty keeps the current language.
    public static void Use(string? code, params string[] langDirs)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        code = code.Trim();
        var before = Language;
        var beforeMap = _map;
        Language = code;
        var dirs = langDirs.Length > 0 ? langDirs : [PackageLangDir, UserLangDir];   // later folders win
        var lang = code.Split('-', '_')[0];
        var merged = new Dictionary<string, string>();
        foreach (var name in new[] { lang, code }.Distinct(StringComparer.OrdinalIgnoreCase))   // the more specific file wins
            foreach (var dir in dirs)
                if (Read(Path.Combine(dir, name + ".json")) is { } map)
                    foreach (var kv in map) merged[kv.Key] = kv.Value;
        _map = merged;   // no file at all: the keys (English) show
        if (!string.Equals(before, Language, StringComparison.Ordinal) || beforeMap.Count != merged.Count) Changed?.Invoke();
    }

    // LLT writes its language code to <LLT data>\lang; a missing or unreadable file falls back to the Windows UI culture.
    public static void FromLlt(string? langFile = null, params string[] langDirs)
    {
        string? code = null;
        try
        {
            var path = langFile ?? Path.Combine(Paths.LltDataDir, "lang");
            if (File.Exists(path)) code = File.ReadAllText(path);
        }
        catch { /* the fallback below */ }
        Use(string.IsNullOrWhiteSpace(code) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : code, langDirs);
    }

    // Test seam: English, no files.
    public static void Reset() { Language = "en"; _map = new Dictionary<string, string>(); }

    static Dictionary<string, string>? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                : null;
        }
        catch { return null; }   // a broken file is skipped, the next candidate is tried
    }
}
