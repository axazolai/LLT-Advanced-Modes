using System.Text.Json;
using System.Text.Json.Serialization;
using LegionModes.Core.Model;

namespace LegionModes.Core.Programs;

public sealed record ProgramsConfig(int SchemaVersion, IReadOnlyList<ProgramEntry> Entries)
{
    public const int CurrentSchema = 2;
}

public static class ProgramsStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static ProgramsConfig Empty() => new(ProgramsConfig.CurrentSchema, []);

    // missing -> empty, no error; schema 1 -> the same patterns and classes as entries + Notice; bad -> empty + error.
    // Never writes.
    public static LoadResult<ProgramsConfig> Load(string path)
    {
        if (!File.Exists(path)) return new(Empty(), null);
        try
        {
            var text = File.ReadAllText(path);
            using (var doc = JsonDocument.Parse(text))
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1)
                    return MigrateV1(doc.RootElement);

            var config = JsonSerializer.Deserialize<ProgramsConfig>(text, Options)
                         ?? throw new JsonException(Tr.T("файл пуст"));
            return Errors(config) is { Count: > 0 } errors ? new(Empty(), errors[0]) : new(config, null);
        }
        catch (Exception e)
        {
            return new(Empty(), Tr.F("не удалось прочитать {0}: {1}", path, e.Message));
        }
    }

    static LoadResult<ProgramsConfig> MigrateV1(JsonElement root)
    {
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return new(Empty(), Tr.T("в файле нет списка программ"));
        var entries = new List<ProgramEntry>();
        foreach (var r in rules.EnumerateArray())
        {
            var pattern = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var cls = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(pattern)) return new(Empty(), Tr.T("в списке программ есть запись без шаблона"));
            if (!Enum.TryParse<ProgramClass>(cls, true, out var pc) || pc is not (ProgramClass.Work or ProgramClass.Heavy))
                return new(Empty(), Tr.F("неизвестный класс «{0}» в схеме 1", cls));
            entries.Add(new ProgramEntry(pattern, pc));
        }
        return new(new ProgramsConfig(ProgramsConfig.CurrentSchema, entries), null, Tr.T("programs.json переведён со схемы 1 на схему 2"));
    }

    // The checks Load applies: empty means the loader accepts the config.
    public static IReadOnlyList<string> Errors(ProgramsConfig c)
    {
        if (c.SchemaVersion != ProgramsConfig.CurrentSchema)
            return [Tr.F("версия схемы {0} не поддерживается (нужна {1})", c.SchemaVersion, ProgramsConfig.CurrentSchema)];
        if (c.Entries is null || c.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Pattern)))
            return [Tr.T("в списке программ есть запись без шаблона")];
        var errors = new List<string>();
        foreach (var e in c.Entries)
        {
            if (!Enum.IsDefined(e.Class)) errors.Add(Tr.F("«{0}»: неизвестный класс", e.Pattern));
            if (e.CommandLine is { } cl && string.IsNullOrWhiteSpace(cl)) errors.Add(Tr.F("«{0}»: пустой шаблон командной строки", e.Pattern));
            if (e.DenyWakeRequests && WakeOverrides.ExeName(e.Pattern) is null)
                errors.Add(Tr.F("«{0}»: запрет удерживать сон работает только для шаблона, который кончается именем exe без масок", e.Pattern));
        }
        return errors;
    }

    // write a temp file, then replace the target
    public static void Save(string path, ProgramsConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
