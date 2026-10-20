namespace LegionModes.Core.Status;

// preset-map.json in the plugin data dir: profile Id -> LLT GodMode preset Guid.
public static class PresetMapFile
{
    // Missing gives an empty map (the first run); an unreadable file gives an empty map and the reason.
    public static (Dictionary<Guid, Guid> Map, string? Error) Load(string path)
    {
        var (value, _, error) = JsonFile.TryLoad<Dictionary<Guid, Guid>>(path);
        return (value ?? [], error);
    }

    public static void Save(string path, IReadOnlyDictionary<Guid, Guid> map) =>
        JsonFile.Save(path, new Dictionary<Guid, Guid>(map));
}
