using System.Security.Cryptography;
using System.Text;
using LegionModes.Core.Engine;

namespace LegionModes.Core.Model;

public enum PresetSourceKind { Factory, CopyOf, Template }

// Where a new preset's values come from: the factory entry named FactoryName, a copy of the preset CopyOf, or the template.
public sealed record PresetSource(PresetSourceKind Kind, Guid? CopyOf = null, string? FactoryName = null);
public sealed record PresetRename(Guid Id, string NewName);
public sealed record PresetCreate(Guid ProfileId, Guid NewId, string Name, PresetSource Source);

// Map = profile Id -> preset Id after the plan; the caller stores it.
public sealed record PresetPlan(IReadOnlyList<PresetRename> Renames, IReadOnlyList<PresetCreate> Creates,
    IReadOnlyList<Guid> Deletes, IReadOnlyDictionary<Guid, Guid> Map)
{
    public bool ChangesPresets => Renames.Count > 0 || Creates.Count > 0 || Deletes.Count > 0;
}

// Plans the plugin's GodMode presets ("AM · <name>") against the profiles. A preset belongs to a profile Id (the map), not to
// a name: a rename moves the preset, a profile whose Id is gone loses it (only when `prune`), and a preset without a mapping is
// never deleted. A preset whose name lacks the prefix is never renamed or deleted.
public static class PresetPlanner
{
    static readonly System.Text.RegularExpressions.Regex Leaf =
        new(@"^(ac|weak|battery)\.(open|closed)\.(heavy|idle|work)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // A preset of the first model: the old label and a leaf key as its text.
    static bool IsFirstModelLeaf(string name) => name.StartsWith(Paths.LegacyPresetPrefix, StringComparison.Ordinal) && Leaf.IsMatch(Paths.PresetText(name));

    public static Guid IdFor(Guid profileId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("LM profile " + profileId.ToString("N"))).AsSpan(0, 16));

    // presets: every preset of godmode.json (id -> name). map: the stored profile Id -> preset Id. active: the active preset.
    // prune: false while modes.json failed to load (the factory set stands in) or the map could not be read.
    public static PresetPlan Plan(ModesConfig config, IReadOnlyDictionary<Guid, string> presets,
        IReadOnlyDictionary<Guid, Guid> map, Guid active, bool prune)
    {
        static bool Ours(string n) => Paths.IsOurs(n);

        // A mapping stands while its preset exists, is ours, and no earlier mapping holds it.
        var live = new Dictionary<Guid, Guid>();
        var held = new HashSet<Guid>();
        foreach (var (profile, preset) in map)
            if (presets.TryGetValue(preset, out var n) && Ours(n) && held.Add(preset)) live[profile] = preset;

        // A profile with no mapping adopts the unmapped preset that carries its name (the first run seeds the map this way).
        foreach (var p in config.Profiles.Where(p => !live.ContainsKey(p.Id)))
        {
            var names = ProfileNames.AllNames(p);
            var hit = presets.Where(kv => Ours(kv.Value) && names.Contains(Paths.PresetText(kv.Value)) && !held.Contains(kv.Key)).Select(kv => kv.Key).OrderBy(g => g)
                .Cast<Guid?>().FirstOrDefault();
            if (hit is { } g) { live[p.Id] = g; held.Add(g); }
        }

        var renames = new List<PresetRename>();
        var pending = config.Profiles.Where(p => live.ContainsKey(p.Id))
            .Select(p => (Id: live[p.Id], Want: ProfileNames.PresetLabel(p))).Where(x => presets[x.Id] != x.Want).ToList();
        var leaving = pending.Select(x => presets[x.Id]).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, want) in pending)
        {
            if (presets.Any(kv => kv.Key != id && kv.Value == want) && !leaving.Contains(want)) continue;   // the name is taken by another preset
            renames.Add(new PresetRename(id, want));
        }

        var deletes = new List<Guid>();
        if (prune)
        {
            var ids = config.Profiles.Select(p => p.Id).ToHashSet();
            foreach (var (profile, preset) in live.ToArray())
            {
                if (ids.Contains(profile) || preset == active) continue;
                deletes.Add(preset);
                live.Remove(profile);
            }
        }

        // Presets of the first model (one per leaf: «LM · ac.open.heavy», 18 of them) belong to no profile and are never applied.
        if (prune)
            foreach (var (id, name) in presets)
                if (id != active && !live.ContainsValue(id) && !deletes.Contains(id) && IsFirstModelLeaf(name)) deletes.Add(id);

        var existing = new Dictionary<Guid, Guid>(live);
        var finalNames = presets.Where(kv => !deletes.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => renames.FirstOrDefault(r => r.Id == kv.Key)?.NewName ?? kv.Value);
        var creates = new List<PresetCreate>();
        var factory = FactoryProfiles.Presets();
        foreach (var p in config.Profiles.Where(p => p.Set.Power is { OwnPreset: true } && !live.ContainsKey(p.Id)).ToArray())
        {
            var name = ProfileNames.PresetLabel(p);
            if (finalNames.ContainsValue(name)) continue;
            var ancestors = Resolver.Ancestors(config, p);
            PresetSource source;
            if (factory.ContainsKey(p.Name)) source = new PresetSource(PresetSourceKind.Factory, FactoryName: p.Name);
            else if (ancestors.Select(a => existing.TryGetValue(a.Id, out var e) ? (Guid?)e : null).FirstOrDefault(e => e is not null) is { } copy)
                source = new PresetSource(PresetSourceKind.CopyOf, CopyOf: copy);
            else if (ancestors.FirstOrDefault(a => factory.ContainsKey(a.Name)) is { } fa)
                source = new PresetSource(PresetSourceKind.Factory, FactoryName: fa.Name);
            else source = new PresetSource(PresetSourceKind.Template);
            var newId = IdFor(p.Id);
            if (finalNames.ContainsKey(newId) || presets.ContainsKey(newId)) newId = Guid.NewGuid();
            creates.Add(new PresetCreate(p.Id, newId, name, source));
            finalNames[newId] = name;
            live[p.Id] = newId;
        }
        return new PresetPlan(renames, creates, deletes, live);
    }
}
