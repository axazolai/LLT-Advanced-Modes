namespace LegionModes.Core.Model;

public static class ProfileNames
{
    public const string Root = "Всегда", Autonomy = "Автономия";

    static readonly System.Text.RegularExpressions.Regex OldLidWord =
        new(@"(?<!\p{L})([Оо]ткрыт|[Зз]акрыт)а(?!\p{L})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Names written before the lid words became «открыт» / «закрыт» (owner 2026-10-20): «Сеть, открыта» -> «Сеть, открыт».
    public static string WithCurrentLidWords(string name) => OldLidWord.Replace(name, "$1");

    // "Сеть, открыт, простой" (english: "AC, open, idle"); axis order source, lid, workload, monitor.
    public static string FromConditions(Conditions c, bool english = false)
    {
        if (english) return FromConditionsEnglish(c);
        var words = new List<string>();
        if (c.Source is { } s)
            words.Add(s switch { PowerSource.Ac => "сеть", PowerSource.Weak => "слабый адаптер", _ => "батарея" });
        if (c.Lid is { } l) words.Add(l == LidState.Open ? "открыт" : "закрыт");
        if (c.Load is { } w)
            words.Add(w switch { Workload.Heavy => "тяжёлая", Workload.Ai => "ИИ", Workload.Work => "работа", _ => "простой" });
        if (c.Monitor is { } m) words.Add(m == MonitorState.Connected ? "монитор" : "без монитора");
        if (words.Count == 0) return Root;
        var text = string.Join(", ", words);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    // The condition-built name in the current language, for display.
    public static string FromConditionsShown(Conditions c) => FromConditions(c, LanguageKey() != "ru");

    static string FromConditionsEnglish(Conditions c)
    {
        var words = new List<string>();
        if (c.Source is { } s)
            words.Add(s switch { PowerSource.Ac => "AC", PowerSource.Weak => "Weak adapter", _ => "Battery" });
        if (c.Lid is { } l) words.Add(c is { Source: null, Load: null, Monitor: null } ? (l == LidState.Open ? "Lid open" : "Lid closed") : l == LidState.Open ? "open" : "closed");
        if (c.Load is { } w)
            words.Add(w switch { Workload.Heavy => "heavy", Workload.Ai => "AI", Workload.Work => "work", _ => "idle" });
        if (c.Monitor is { } m) words.Add(m == MonitorState.Connected ? "monitor" : "no monitor");
        return words.Count == 0 ? "Always" : string.Join(", ", words);
    }

    // The language key of the current interface language: "en", "ru", "de" (the language part of the culture code).
    public static string LanguageKey()
    {
        var code = Tr.Language;
        var cut = code.IndexOfAny(['-', '_']);
        return (cut > 0 ? code[..cut] : code).ToLowerInvariant();
    }

    // The name shown for a profile in the current language: its own text for the language, else the English one, else the
    // name it was created with. Profiles of the built-in set have an English name even in files written before the names
    // were kept per language.
    public static string Display(Profile p)
    {
        var key = LanguageKey();
        if (p.Names is { Count: > 0 } n)
        {
            if (n.TryGetValue(key, out var own) && own.Length > 0) return own;
            if (n.TryGetValue("en", out var en) && en.Length > 0) return en;
        }
        return key == "ru" ? p.Name : BuiltInEnglish(p.Name) ?? p.Name;
    }

    // The GodMode preset label of a profile in the current language.
    public static string PresetLabel(Profile p) => Paths.PresetPrefix + Display(p);

    // Every name the profile has had or has in any language (a preset label may carry any of them).
    public static IReadOnlyCollection<string> AllNames(Profile p)
    {
        var all = new HashSet<string>(StringComparer.Ordinal) { p.Name };
        if (p.Names is not null) foreach (var v in p.Names.Values) all.Add(v);
        if (BuiltInEnglish(p.Name) is { } en) all.Add(en);
        return all;
    }

    // For a name without its profile at hand (a stored text, a message): only the built-in names are known.
    public static string DisplayName(string name) => LanguageKey() == "ru" ? name : BuiltInEnglish(name) ?? name;

    // The English name of a built-in profile, by its Russian name; null for any other name.
    public static string? BuiltInEnglish(string name)
    {
        if (name == Root) return "Always";
        if (name == Autonomy) return "Autonomy";
        if (name == "Закрыт") return "Lid closed";
        var parts = name.Split(", ");
        var words = new List<string>();
        foreach (var part in parts)
        {
            var word = part.ToLowerInvariant() switch
            {
                "сеть" => "AC", "слабый адаптер" => "Weak adapter", "батарея" => "Battery", "открыт" => "open", "закрыт" => "closed",
                "тяжёлая" => "heavy", "ии" => "AI", "работа" => "work", "простой" => "idle", "монитор" => "monitor",
                "без монитора" => "no monitor", _ => null,
            };
            if (word is null) return null;
            words.Add(word);
        }
        return string.Join(", ", words);
    }

    // The profile with its name set in the current language. Russian also sets the name the profile is known by (the preset
    // «AM · name» follows); another language only adds its own text.
    public static Profile WithName(Profile p, string text)
    {
        var key = LanguageKey();
        var names = new Dictionary<string, string>(p.Names ?? new Dictionary<string, string>());
        names[key] = text;
        return key == "ru" ? p with { Name = text, Names = names } : p with { Names = names };
    }
}
