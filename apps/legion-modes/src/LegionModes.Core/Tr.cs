using System.Globalization;
using System.Text.Json;

namespace LegionModes.Core;

// The owner-facing text. The Russian source text is the key: Tr.T("текст") returns it as is for Russian and the
// translation from <code>.json otherwise, a flat JSON object {"русский текст": "translation"}. Two folders: the owner's own
// (<LLT data>\Plugins\Configs\LegionModes\lang, kept by every update) and the package's (<plugin dir>\lang, mirrored
// from the package). For a culture code the files en, the language ("de") and the full code ("de-DE") are laid over each
// other, the owner's folder over the package's; a key found nowhere keeps the Russian text.
public static class Tr
{
    static IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>();

    public static string Language { get; private set; } = "ru";

    // Raised (on the calling thread) when Use changed the language or the texts.
    public static event Action? Changed;

    public static bool IsRussian => Language == "ru";

    public static string UserLangDir { get; } = Path.Combine(Paths.LltDataDir, "Plugins", "Configs", Paths.PluginId, "lang");

    public static string PackageLangDir { get; } = Path.Combine(Path.GetDirectoryName(Paths.PluginDll)!, "lang");

    public static string T(string ru) => _map.TryGetValue(ru, out var t) && t.Length > 0 ? t : ru;

    // The text carries {0}, {1}... placeholders; the translation keeps the same ones.
    public static string F(string ru, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(ru), args);

    // A culture code ("ru", "ru-RU", "en", "de-DE", ...); null or empty keeps the current language.
    public static void Use(string? code, params string[] langDirs)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        code = code.Trim();
        var before = Language;
        var beforeMap = _map;
        if (code.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            Language = "ru"; _map = new Dictionary<string, string>();
            if (before != Language) Changed?.Invoke();
            return;
        }
        Language = code;
        var dirs = langDirs.Length > 0 ? langDirs : [PackageLangDir, UserLangDir];   // later folders win
        var lang = code.Split('-', '_')[0];
        var merged = new Dictionary<string, string>();
        foreach (var name in new[] { "en", lang, code }.Distinct(StringComparer.OrdinalIgnoreCase))   // the more specific file wins
            foreach (var dir in dirs)
                if (Read(Path.Combine(dir, name + ".json")) is { } map)
                    foreach (var kv in map) merged[kv.Key] = kv.Value;
        _map = merged;   // no file at all: the Russian text stays
        if (before != Language || beforeMap.Count != merged.Count) Changed?.Invoke();
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

    // Test seam: restores the default.
    public static void Reset() { Language = "ru"; _map = new Dictionary<string, string>(); }

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
