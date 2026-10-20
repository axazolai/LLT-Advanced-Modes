using System.IO;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace LegionModes.Plugin.Backlight;

// lighting-profiles.json: the six Spectrum keyboard profile slots, so the owner's own effects can be put back after the
// plugin has written its profile choice. Serialised like LLT serialises them.
public static class LightingStore
{
    public sealed class Entry
    {
        public int Profile { get; set; }
        public SpectrumKeyboardBacklightEffect[] Effects { get; set; } = [];
    }

    // TypeNameHandling.Auto reads "$type" from the file: only the lighting types may be created from it.
    sealed class LightingBinder : ISerializationBinder
    {
        static readonly HashSet<Type> Allowed =
        [
            typeof(Entry), typeof(List<Entry>), typeof(Entry[]),
            typeof(SpectrumKeyboardBacklightEffect), typeof(SpectrumKeyboardBacklightEffect[]),
            typeof(RGBColor), typeof(RGBColor[]), typeof(ushort[]),
            typeof(SpectrumKeyboardBacklightEffectType), typeof(SpectrumKeyboardBacklightSpeed),
            typeof(SpectrumKeyboardBacklightDirection), typeof(SpectrumKeyboardBacklightClockwiseDirection),
        ];

        public Type BindToType(string? assemblyName, string typeName)
        {
            var type = Type.GetType(assemblyName is null ? typeName : $"{typeName}, {assemblyName}", throwOnError: false);
            if (type is null || !Allowed.Contains(type))
                throw new JsonSerializationException($"type \"{typeName}\" is not allowed in lighting-profiles.json");
            return type;
        }

        public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
        {
            assemblyName = serializedType.Assembly.FullName;
            typeName = serializedType.FullName;
        }
    }

    static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        TypeNameHandling = TypeNameHandling.Auto,
        SerializationBinder = new LightingBinder(),
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Converters = [new StringEnumConverter()],
    };

    const int ReadAttempts = 5;
    static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(300);

    // Reads all six slots, then writes the file; a failing slot writes nothing. LLT's feature request/response pair has no
    // lock, so a concurrent reader (LLT's own UI) can hand back another slot's answer: a slot counts only when the keyboard
    // answers with the slot asked for.
    public static async Task SnapshotAsync(string path)
    {
        var controller = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();
        var entries = new List<Entry>();
        for (var profile = 1; profile <= 6; profile++)
        {
            for (var attempt = 1; ; attempt++)
            {
                var (p, effects) = await controller.GetProfileDescriptionAsync(profile).ConfigureAwait(false);
                if (p == profile) { entries.Add(new Entry { Profile = p, Effects = effects }); break; }
                if (attempt == ReadAttempts)
                    throw new InvalidOperationException($"keyboard slot {profile}: the answer was slot {p} in {ReadAttempts} reads; nothing written");
                await Task.Delay(ReadRetryDelay).ConfigureAwait(false);
            }
        }
        var json = JsonConvert.SerializeObject(entries, Settings);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task RestoreAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var entries = JsonConvert.DeserializeObject<List<Entry>>(json, Settings)
                      ?? throw new InvalidOperationException("lighting-profiles.json is empty");
        var controller = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();
        var current = await controller.GetProfileAsync().ConfigureAwait(false);
        foreach (var e in entries)
            await controller.SetProfileDescriptionAsync(e.Profile, e.Effects).ConfigureAwait(false);
        await controller.SetProfileAsync(current).ConfigureAwait(false);   // writing a slot can leave another one selected
    }

    // The first runtime start: a snapshot when none exists and the keyboard is supported. Returns an error text or null.
    public static async Task<string?> SnapshotIfMissingAsync(string path)
    {
        if (File.Exists(path)) return null;
        var controller = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();
        if (!await controller.IsSupportedAsync().ConfigureAwait(false)) return null;
        await SnapshotAsync(path).ConfigureAwait(false);
        return null;
    }
}
