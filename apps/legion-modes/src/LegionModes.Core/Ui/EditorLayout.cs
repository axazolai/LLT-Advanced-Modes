using System.Text.Json;

namespace LegionModes.Core.Ui;

// Where the profile editor's blocks sit: three columns of block keys, top to bottom. One layout for every profile (owner).
public sealed record EditorLayout(IReadOnlyList<IReadOnlyList<string>> Columns)
{
    public const string Profile = "profile", Overlaps = "overlaps", Actions = "actions";

    // Owner 2026-10-19: left Профиль, Пересечения, Таймеры простоя, Действия LLT; right Мощность и вентиляторы,
    // Процессор и схема Windows, Режим видеокарты, Частота встроенного экрана, Внешний монитор; the rest in the middle.
    public static EditorLayout Default { get; } = new(
    [
        [Profile, Overlaps, "Timers", Actions],
        ["Charging", "Usb", "Lighting"],
        ["Power", "Processor", "Gpu", "Panel", "External"],
    ]);

    public static IReadOnlyList<string> Keys { get; } = Default.Columns.SelectMany(c => c).ToList();

    // Unknown keys dropped, repeats dropped (the first stays), missing keys added at the end of their default column;
    // anything but three columns gives the default.
    public static EditorLayout Normalize(EditorLayout? layout)
    {
        if (layout?.Columns is not { Count: 3 } columns) return Default;
        var seen = new HashSet<string>();
        var result = columns.Select(c => (c ?? []).Where(k => Keys.Contains(k) && seen.Add(k)).ToList()).ToList();
        for (var i = 0; i < 3; i++)
            foreach (var k in Default.Columns[i].Where(k => !seen.Contains(k)))
                result[i].Add(k);
        return new EditorLayout(result);
    }

    // Missing or unreadable -> the default; never throws.
    public static EditorLayout Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Default;
            var columns = JsonSerializer.Deserialize<List<List<string>>>(File.ReadAllText(path));
            return Normalize(columns is null ? null : new EditorLayout(columns));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return Default;
        }
    }

    public static void Save(string path, EditorLayout layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(layout.Columns, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
