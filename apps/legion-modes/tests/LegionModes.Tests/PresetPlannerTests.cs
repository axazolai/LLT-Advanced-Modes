using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Core.Status;

namespace LegionModes.Tests;

public sealed class PresetPlannerTests : IDisposable
{
    static readonly ModesConfig Factory = FactoryProfiles.Create();
    static readonly Guid None = Guid.Empty;
    const string Prefix = Paths.PresetPrefix;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "LegionModes.Presets." + Guid.NewGuid().ToString("N"));

    public PresetPlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static Profile Find(ModesConfig c, string name) => c.Profiles.Single(p => p.Name == name);

    static ModesConfig Rename(ModesConfig c, string from, string to) =>
        c with { Profiles = c.Profiles.Select(p => p.Name == from ? p with { Name = to, Names = new Dictionary<string, string> { ["ru"] = to } } : p).ToArray() };

    static ModesConfig Without(ModesConfig c, string name) =>
        c with { Profiles = c.Profiles.Where(p => p.Name != name).ToArray() };

    // A godmode.json that holds one plugin preset per own-preset profile of the config, named after the profile.
    static (Dictionary<Guid, string> Presets, Dictionary<Guid, Guid> Map) Seeded(ModesConfig c)
    {
        var presets = new Dictionary<Guid, string>();
        var map = new Dictionary<Guid, Guid>();
        foreach (var p in c.Profiles.Where(p => p.Set.Power is { OwnPreset: true }))
        {
            var id = Guid.NewGuid();
            presets[id] = Prefix + p.Name;
            map[p.Id] = id;
        }
        return (presets, map);
    }

    [Fact(DisplayName = "@critical a renamed profile keeps its tuned preset: the preset is renamed by profile Id, never recreated and never deleted")]
    public void RenameIsCarriedById()
    {
        var (presets, map) = Seeded(Factory);
        var renamed = Rename(Factory, "Батарея", "Батарея-2");
        var preset = map[Find(Factory, "Батарея").Id];

        var plan = PresetPlanner.Plan(renamed, presets, map, None, prune: true);

        Assert.Equal([new PresetRename(preset, Prefix + "Батарея-2")], plan.Renames);
        Assert.Empty(plan.Creates);
        Assert.Empty(plan.Deletes);
        Assert.Equal(preset, plan.Map[Find(renamed, "Батарея-2").Id]);
    }

    [Fact(DisplayName = "@critical a preset takes the profile's name in the current language, and a preset of the old LM label is renamed to the AM label")]
    public void LabelFollowsLanguageAndLegacyPrefix()
    {
        var bat = Find(Factory, "Батарея");
        var presets = new Dictionary<Guid, string> { [Guid.NewGuid()] = Paths.LegacyPresetPrefix + "Батарея" };
        var map = new Dictionary<Guid, Guid> { [bat.Id] = presets.Keys.Single() };
        try
        {
            Tr.Use("en", Path.GetTempPath());
            var plan = PresetPlanner.Plan(Factory, presets, map, None, prune: false);
            Assert.Contains(new PresetRename(presets.Keys.Single(), Prefix + "Battery"), plan.Renames);
            Assert.DoesNotContain(plan.Creates, c => c.ProfileId == bat.Id);

            var unmapped = PresetPlanner.Plan(Factory, presets, new Dictionary<Guid, Guid>(), None, prune: false);   // adopted by name, not created again
            Assert.Equal(presets.Keys.Single(), unmapped.Map[bat.Id]);
        }
        finally { TestLang.Russian(); }
    }

    [Fact(DisplayName = "@critical presets of the first model (LM label, leaf key) are removed when the config loaded; other unmapped presets stay")]
    public void FirstModelLeavesAreRemoved()
    {
        var (presets, map) = Seeded(Factory);
        var leaf = Guid.NewGuid(); presets[leaf] = Paths.LegacyPresetPrefix + "ac.open.heavy";
        var activeLeaf = Guid.NewGuid(); presets[activeLeaf] = Paths.LegacyPresetPrefix + "weak.closed.work";
        var mine = Guid.NewGuid(); presets[mine] = Paths.LegacyPresetPrefix + "моя заметка";
        var foreign = Guid.NewGuid(); presets[foreign] = "ac.open.heavy";

        var plan = PresetPlanner.Plan(Factory, presets, map, activeLeaf, prune: true);
        Assert.Equal([leaf], plan.Deletes);
        Assert.Empty(PresetPlanner.Plan(Factory, presets, map, activeLeaf, prune: false).Deletes);   // modes.json did not load
    }

    [Fact(DisplayName = "@critical a preset is deleted only with its profile Id, only for a config that loaded, never the active one and never an unmapped one")]
    public void DeleteNeedsMappingAndAGoneProfile()
    {
        var (presets, map) = Seeded(Factory);
        var unmapped = Guid.NewGuid();
        presets[unmapped] = Prefix + "Старый, без записи";
        var foreign = Guid.NewGuid();
        presets[foreign] = "Пресет владельца";
        var gone = map[Find(Factory, "Батарея").Id];
        var config = Without(Factory, "Батарея");

        var pruned = PresetPlanner.Plan(config, presets, map, None, prune: true);
        Assert.Equal([gone], pruned.Deletes);
        Assert.DoesNotContain(Find(Factory, "Батарея").Id, pruned.Map.Keys);

        Assert.Empty(PresetPlanner.Plan(config, presets, map, None, prune: false).Deletes);   // modes.json did not load
        var keptActive = PresetPlanner.Plan(config, presets, map, gone, prune: true);
        Assert.Empty(keptActive.Deletes);                                                      // the active preset stays
        Assert.Contains(Find(Factory, "Батарея").Id, keptActive.Map.Keys);
        Assert.DoesNotContain(unmapped, pruned.Deletes);
        Assert.DoesNotContain(foreign, pruned.Deletes);
    }

    [Fact(DisplayName = "@critical a preset whose name lacks the prefix is never renamed or deleted, even when a map entry points at it")]
    public void ForeignPresetIsUntouched()
    {
        var (presets, map) = Seeded(Factory);
        var bat = Find(Factory, "Батарея");
        var foreign = Guid.NewGuid();
        presets[foreign] = "Пресет владельца";
        presets.Remove(map[bat.Id]);
        map[bat.Id] = foreign;                                  // a stale map entry
        var removed = Find(Factory, "Слабый адаптер");
        map[removed.Id] = foreign;                              // two entries on one preset
        var config = Without(Factory, "Слабый адаптер");

        var plan = PresetPlanner.Plan(Rename(config, "Батарея", "Батарея-2"), presets, map, None, prune: true);

        Assert.Empty(plan.Renames);
        Assert.Empty(plan.Deletes);
        Assert.DoesNotContain(foreign, plan.Map.Values);
    }

    [Fact(DisplayName = "@important the first run seeds the map by name; an unmapped profile adopts the preset that carries its name")]
    public void AdoptionByName()
    {
        var (presets, _) = Seeded(Factory);
        var bat = Find(Factory, "Батарея");
        var preset = presets.Single(kv => kv.Value == Prefix + "Батарея").Key;

        var plan = PresetPlanner.Plan(Factory, presets, new Dictionary<Guid, Guid>(), None, prune: true);

        Assert.Equal(preset, plan.Map[bat.Id]);
        Assert.False(plan.ChangesPresets);
        Assert.Equal(presets.Count, plan.Map.Count);
    }

    [Fact(DisplayName = "@important a new own preset takes the profile's own factory values, else the nearest ancestor's existing preset, else the ancestor's factory values")]
    public void CreateSourceOrder()
    {
        var (presets, map) = Seeded(Factory);
        var bat = Find(Factory, "Батарея");
        var parent = map[bat.Id];
        var mine = bat with { Id = Guid.NewGuid(), Name = "Моя батарея", Names = null, Set = bat.Set with { Power = PowerSetting.Own } };
        var withMine = Factory with { Profiles = [.. Factory.Profiles, mine] };

        // not a factory name: the ancestor's tuned preset wins over any factory entry
        var copy = PresetPlanner.Plan(withMine, presets, map, None, prune: true).Creates.Single();
        Assert.Equal(new PresetSource(PresetSourceKind.CopyOf, CopyOf: parent), copy.Source);
        Assert.Equal(Prefix + "Моя батарея", copy.Name);

        // the ancestor has no preset: its factory values
        presets.Remove(parent);
        map.Remove(bat.Id);
        presets.Remove(map[Factory.Root.Id]);                  // the root's preset is an ancestor's too
        map.Remove(Factory.Root.Id);
        var fromFactory = PresetPlanner.Plan(Without(withMine, "Батарея"), presets, map, None, prune: true).Creates
            .Single(c => c.ProfileId == mine.Id);
        Assert.Equal(PresetSourceKind.Factory, fromFactory.Source.Kind);

        // a factory name: its own values, whatever the ancestors hold
        var (presets2, map2) = Seeded(Factory);
        var heavy = Find(Factory, "Батарея, открыт, тяжёлая");
        presets2.Remove(map2[heavy.Id]);
        map2.Remove(heavy.Id);
        var own = PresetPlanner.Plan(Factory, presets2, map2, None, prune: true).Creates.Single();
        Assert.Equal(new PresetSource(PresetSourceKind.Factory, FactoryName: heavy.Name), own.Source);
    }

    [Fact(DisplayName = "@important a created preset gets an id derived from the profile Id, and a free one when that id is taken")]
    public void CreatedIdIsStableAndFree()
    {
        var (presets, map) = Seeded(Factory);
        var bat = Find(Factory, "Батарея");
        presets.Remove(map[bat.Id]);
        map.Remove(bat.Id);

        var plan = PresetPlanner.Plan(Factory, presets, map, None, prune: true);
        Assert.Equal(PresetPlanner.IdFor(bat.Id), plan.Creates.Single().NewId);

        presets[PresetPlanner.IdFor(bat.Id)] = "Пресет владельца";   // the derived id is another preset's
        var taken = PresetPlanner.Plan(Factory, presets, map, None, prune: true).Creates.Single();
        Assert.NotEqual(PresetPlanner.IdFor(bat.Id), taken.NewId);
    }

    [Fact(DisplayName = "@important preset-map.json: a missing file is an empty map, an unreadable one is reported and never replaced by an empty map")]
    public void MapFileMissingAndUnreadable()
    {
        var path = Path.Combine(_dir, "preset-map.json");
        var (empty, noError) = PresetMapFile.Load(path);
        Assert.Empty(empty);
        Assert.Null(noError);

        var id = (Guid.NewGuid(), Guid.NewGuid());
        PresetMapFile.Save(path, new Dictionary<Guid, Guid> { [id.Item1] = id.Item2 });
        Assert.Equal(id.Item2, PresetMapFile.Load(path).Map[id.Item1]);

        File.WriteAllText(path, "{ not json");
        var (broken, error) = PresetMapFile.Load(path);
        Assert.Empty(broken);
        Assert.NotNull(error);
    }
}
