using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegionModes.Core.Model;

public static class ModesStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // missing -> factory, no error; schemaVersion 1 -> factory + Notice; invalid JSON / other schema / validator errors ->
    // factory + Error. Never writes.
    public static LoadResult<ModesConfig> Load(string path, int maxBrightness)
    {
        if (!File.Exists(path)) return new(FactoryProfiles.Create(), null);
        try
        {
            var text = File.ReadAllText(path);
            int? schema;
            using (var doc = JsonDocument.Parse(text))
                schema = doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("schemaVersion", out var v) && v.TryGetInt32(out var n) ? n : null;
            if (schema == 1)
                return new(FactoryProfiles.Create(), null, Tr.T("schema 1 profiles replaced with the factory set"), "modes.schema1.json");
            if (schema != ModesConfig.CurrentSchema)
                return new(FactoryProfiles.Create(),
                    Tr.F("schema version {0} is not supported ({1} required)", schema?.ToString() ?? Tr.T("(not set)"), ModesConfig.CurrentSchema));

            var config = JsonSerializer.Deserialize<ModesConfig>(text, Options) ?? throw new JsonException(Tr.T("the file is empty"));
            var error = ModesValidator.Errors(config, maxBrightness).FirstOrDefault();
            if (error is not null) return new(FactoryProfiles.Create(), error);
            // The old lid words in profile names are replaced, unless that would make the config invalid (a name taken).
            var renamed = config with { Profiles = config.Profiles.Select(p => p with { Name = ProfileNames.WithCurrentLidWords(p.Name) }).ToList() };
            if (renamed.Profiles.SequenceEqual(config.Profiles) || ModesValidator.Errors(renamed, maxBrightness).Count > 0) return new(config, null);
            return new(renamed, null, Tr.T("profile names: the old lid words were replaced with the current ones"), "modes.lid-words.json");
        }
        catch (Exception e)
        {
            return new(FactoryProfiles.Create(), Tr.F("could not read {0}: {1}", path, e.Message));
        }
    }

    // write a temp file, then replace the target
    public static void Save(string path, ModesConfig config)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
