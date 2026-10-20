using LegionModes.Core.Model;

namespace LegionModes.Tests;

public sealed class ProfileModelTests : IDisposable
{
    const int MaxBrightness = 100;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "legionmodes-" + Guid.NewGuid().ToString("N"));

    public ProfileModelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    static Profile Find(ModesConfig c, string name) => c.Profiles.Single(p => p.Name == name);

    static ModesConfig Replace(ModesConfig c, string name, Func<Profile, Profile> edit) =>
        c with { Profiles = c.Profiles.Select(p => p.Name == name ? edit(p) : p).ToArray() };

    static ModesConfig Add(ModesConfig c, Profile p) => c with { Profiles = c.Profiles.Append(p).ToArray() };

    static Profile Extra(string name, Conditions cond, Parameters? set = null) =>
        new(Guid.NewGuid(), name, ProfileKind.Automatic, cond, 0, 0, set ?? new Parameters(), [], []);

    static void AssertRefused(ModesConfig c, string cause) =>
        Assert.Contains(ModesValidator.Errors(c, MaxBrightness), e => e.Contains(cause));

    static IdleTimers RootTimers(ModesConfig c) => c.Root.Set.Timers!;

    string Path_(string name) => Path.Combine(_dir, name);

    string Json(ModesConfig c)
    {
        var p = Path_("json-" + Guid.NewGuid().ToString("N") + ".json");
        ModesStore.Save(p, c);
        return File.ReadAllText(p);
    }

    [Fact(DisplayName = "@critical factory set: valid, 24 profiles in the order of mode-defaults, presets for exactly the own-preset profiles")]
    public void FactorySetIsValidAndComplete()
    {
        var c = FactoryProfiles.Create();
        Assert.Empty(ModesValidator.Errors(c, MaxBrightness));
        Assert.Equal(24, c.Profiles.Count);
        Assert.Equal(ProfileNames.Root, c.Profiles[0].Name);
        Assert.Equal(ProfileKind.Root, c.Profiles[0].Kind);
        Assert.Equal(ProfileNames.Autonomy, c.Autonomy.Name);
        Assert.Equal(1, c.Profiles.Count(p => p.Kind == ProfileKind.Manual));

        string[] expected =
        [
            "Всегда", "Закрыт", "Слабый адаптер", "Батарея", "Сеть, закрыт", "Слабый адаптер, закрыт", "Батарея, закрыт",
            "Сеть, открыт, тяжёлая", "Сеть, открыт, ИИ", "Сеть, открыт, работа", "Сеть, открыт, простой",
            "Сеть, закрыт, тяжёлая", "Сеть, закрыт, работа", "Сеть, закрыт, простой",
            "Слабый адаптер, открыт, тяжёлая", "Слабый адаптер, открыт, работа", "Слабый адаптер, открыт, простой",
            "Батарея, открыт, тяжёлая", "Батарея, открыт, работа", "Батарея, открыт, простой",
            "Батарея, закрыт, тяжёлая", "Батарея, закрыт, работа", "Батарея, закрыт, простой", "Автономия",
        ];
        Assert.Equal(expected, c.Profiles.Select(p => p.Name));
        foreach (var p in c.Profiles.Where(p => p.Kind == ProfileKind.Automatic))
            Assert.Equal(p.Name, ProfileNames.FromConditions(p.Conditions));

        var own = c.Profiles.Where(p => p.Set.Power?.OwnPreset == true).Select(p => p.Name).ToHashSet();
        var presets = FactoryProfiles.Presets();
        Assert.Equal(own, presets.Keys.ToHashSet());
        Assert.Equal(16, presets.Count);
        foreach (var (name, preset) in presets) Assert.Empty(PresetValidator.Errors(preset));

        Assert.Equal(c.Profiles.Select(p => p.Id), FactoryProfiles.Create().Profiles.Select(p => p.Id));
    }

    [Fact(DisplayName = "@important factory spot values equal refs/mode-defaults.md")]
    public void FactorySpotValues()
    {
        var c = FactoryProfiles.Create();
        var heavy = Find(c, "Сеть, открыт, тяжёлая");
        var pr = FactoryProfiles.Presets()["Сеть, открыт, тяжёлая"];
        Assert.Equal((90, 110, 130, 65, 90, 15, 130, 83, 60, 0),
            (pr.CpuLongW, pr.CpuShortW, pr.CpuPeakW, pr.CpuCrossW, pr.CpuTempC, pr.DynamicBoostW, pr.ConfigurableTgpW, pr.GpuTempC, pr.TppW, pr.CurveOptimizer));
        Assert.Equal([3, 3, 3, 4, 5, 6, 7, 8, 9, 10], pr.FanCurve);
        Assert.Equal(new PanelRate(PanelRateKind.Fixed, 240), heavy.Set.Panel);
        Assert.Equal(60, heavy.HoldSec);

        var batIdle = Find(c, "Батарея, открыт, простой");
        Assert.Equal(new ProcessorScheme(BoostMode.Disabled, 100, WindowsPowerMode.BestPowerEfficiency), batIdle.Set.Processor);
        Assert.Equal(new IdleTimers(new DimStage(1, 15), 3, 10, 60, null), batIdle.Set.Timers);

        var closed = Find(c, "Закрыт");
        Assert.Equal(1, closed.Set.Timers!.ForcedSleepMin);
        Assert.Equal(PanelRateKind.DoNotTouch, closed.Set.Panel!.Kind);

        var auto = c.Autonomy;
        Assert.Equal(new DimStage(0, 30), auto.Set.Timers!.Dim);
        Assert.Equal(UsbPower.Off, auto.Set.Usb);
    }

    [Fact(DisplayName = "@important ProfileNames.FromConditions words and axis order")]
    public void NamesFromConditions()
    {
        Assert.Equal("Сеть, открыт, простой", ProfileNames.FromConditions(new(PowerSource.Ac, LidState.Open, Workload.Idle)));
        Assert.Equal("Закрыт", ProfileNames.FromConditions(new(Lid: LidState.Closed)));
        Assert.Equal("Слабый адаптер, закрыт", ProfileNames.FromConditions(new(PowerSource.Weak, LidState.Closed)));
        Assert.Equal("Сеть, монитор", ProfileNames.FromConditions(new(PowerSource.Ac, Monitor: MonitorState.Connected)));
    }

    [Fact(DisplayName = "@critical ModesValidator refuses each broken rule with a message naming the cause")]
    public void ValidatorRefusals()
    {
        var f = FactoryProfiles.Create();

        AssertRefused(Add(f, Extra("Копия", new(PowerSource.Ac, LidState.Open, Workload.Idle))), "такие же условия у профиля");
        AssertRefused(Replace(f, ProfileNames.Root, p => p with { Conditions = new(Lid: LidState.Open) }), "у корневого профиля не может быть условий");
        AssertRefused(f with { Profiles = new[] { f.Profiles[1], f.Profiles[0] }.Concat(f.Profiles.Skip(2)).ToArray() },
            "корневой профиль должен стоять первым");
        AssertRefused(Replace(f, ProfileNames.Root, p => p with { Set = p.Set with { Gpu = null } }), "должен задавать параметр «Режим видеокарты»");
        AssertRefused(f with { Profiles = f.Profiles.Where(p => p.Kind != ProfileKind.Manual).ToArray() }, "«Автономия» должен быть ровно один");
        AssertRefused(Replace(f, ProfileNames.Autonomy, p => p with { Conditions = new(Lid: LidState.Open) }), "у ручного профиля не может быть условий");
        AssertRefused(Add(f, Extra("Пустая", new Conditions())), "нужно хотя бы одно условие");

        var t = RootTimers(f);
        AssertRefused(Replace(f, ProfileNames.Root, p => p with { Set = p.Set with { Timers = t with { Dim = new DimStage(10, 20), DisplayOffMin = 5 } } }),
            "затемнение (10 мин) должно наступать раньше, чем выключение экрана (5 мин)");
        AssertRefused(Replace(f, ProfileNames.Root, p => p with { Set = p.Set with { Timers = t with { DisplayOffMin = 10, SleepMin = 10 } } }),
            "выключение экрана (10 мин) должно наступать раньше, чем сон (10 мин)");
        AssertRefused(Replace(f, ProfileNames.Root, p => p with { Set = p.Set with { Timers = t with { Dim = new DimStage(5, 101) } } }),
            "яркость затемнения 101");
        AssertRefused(Replace(f, ProfileNames.Root, p => p with { Set = p.Set with { Timers = t with { ForcedSleepMin = 1 } } }),
            "сон с закрытой крышкой задаётся только в профиле с условием «закрыт»");

        var preset = FactoryProfiles.Presets()["Сеть, открыт, тяжёлая"];
        Assert.Contains(PresetValidator.Errors(preset with { ConfigurableTgpW = 82 }), e => e.Contains("Настраиваемый TGP, Вт: 82"));
        var curve = (int[])preset.FanCurve.Clone();
        curve[8] = 2;
        Assert.Contains(PresetValidator.Errors(preset with { FanCurve = curve }), e => e.Contains("точка 9"));
        Assert.Contains(PresetValidator.Errors(preset with { CurveOptimizer = 21 }), e => e.Contains("Curve Optimizer: 21"));
    }

    [Fact(DisplayName = "@important equal conditions are found whatever the field order; profiles differing only in the monitor axis pass")]
    public void ConditionsEqualityAndMonitorAxis()
    {
        var f = FactoryProfiles.Create();
        var reordered = new Conditions(Load: Workload.Idle, Lid: LidState.Open, Source: PowerSource.Ac);
        AssertRefused(Add(f, Extra("Копия", reordered)), "такие же условия у профиля");

        var withMonitor = reordered with { Monitor = MonitorState.Connected };
        Assert.Empty(ModesValidator.Errors(Add(f, Extra("Сеть, открыт, простой, монитор", withMonitor)), MaxBrightness));
    }

    [Fact(DisplayName = "@critical ModesStore.Load: bad JSON, missing file, schema 1, schema 3 and a validator failure all fall back to the factory set")]
    public void StoreLoadFallbacks()
    {
        var factoryJson = Json(FactoryProfiles.Create());

        var bad = Path_("bad.json");
        File.WriteAllText(bad, "{ not json");
        var before = File.ReadAllBytes(bad);
        var r = ModesStore.Load(bad, MaxBrightness);
        Assert.NotNull(r.Error);
        Assert.Equal(factoryJson, Json(r.Value));
        Assert.Equal(before, File.ReadAllBytes(bad));

        r = ModesStore.Load(Path_("missing.json"), MaxBrightness);
        Assert.Null(r.Error);
        Assert.Null(r.Notice);
        Assert.Equal(factoryJson, Json(r.Value));

        var v1 = Path_("v1.json");
        File.WriteAllText(v1, """{"schemaVersion":1,"profiles":[],"leaves":{},"pauseMinutes":60}""");
        r = ModesStore.Load(v1, MaxBrightness);
        Assert.Null(r.Error);
        Assert.Contains("схемы 1", r.Notice);
        Assert.Equal(factoryJson, Json(r.Value));

        var v3 = Path_("v3.json");
        File.WriteAllText(v3, """{"schemaVersion":3,"profiles":[]}""");
        r = ModesStore.Load(v3, MaxBrightness);
        Assert.Contains("3", r.Error);
        Assert.Equal(factoryJson, Json(r.Value));

        var invalid = FactoryProfiles.Create() with { PauseMinutes = 0 };
        var invalidPath = Path_("invalid.json");
        ModesStore.Save(invalidPath, invalid);
        r = ModesStore.Load(invalidPath, MaxBrightness);
        Assert.Equal(ModesValidator.Errors(invalid, MaxBrightness)[0], r.Error);
        Assert.Equal(factoryJson, Json(r.Value));
    }

    [Fact(DisplayName = "@important Save then Load round-trips the factory set and a changed copy exactly")]
    public void StoreRoundTrip()
    {
        var factory = FactoryProfiles.Create();
        var changed = Replace(factory with { PauseMinutes = 30 }, "Сеть, открыт, работа", p => p with
        {
            HoldSec = 90, Conditions = p.Conditions with { Monitor = MonitorState.Connected },
            Set = p.Set with { Timers = new IdleTimers(new DimStage(2, 40), 4, 8, 0, null), Power = new PowerSetting(FirmwareMode.Quiet) },
        });
        Assert.Empty(ModesValidator.Errors(changed, MaxBrightness));

        foreach (var c in new[] { factory, changed })
        {
            var path = Path_("rt.json");
            ModesStore.Save(path, c);
            var loaded = ModesStore.Load(path, MaxBrightness);
            Assert.Null(loaded.Error);
            Assert.Null(loaded.Notice);
            Assert.Equal(Json(c), Json(loaded.Value));
            Assert.Equal(c.Profiles.Select(p => p.Id), loaded.Value.Profiles.Select(p => p.Id));
        }
        var reloaded = ModesStore.Load(Path_("rt.json"), MaxBrightness).Value;
        Assert.Equal(30, reloaded.PauseMinutes);
        Assert.Equal(new PowerSetting(FirmwareMode.Quiet), Find(reloaded, "Сеть, открыт, работа").Set.Power);
    }

    [Fact(DisplayName = "@important ModesStore.Load: old lid words in profile names become «открыт» / «закрыт» with a notice and a backup name; a taken name stays")]
    public void StoreLoadRenamesOldLidWords()
    {
        var factory = FactoryProfiles.Create();
        var old = Replace(Replace(factory, "Сеть, открыт, работа", p => p with { Name = "Сеть, открыта, работа" }), "Закрыт", p => p with { Name = "Закрыта" });
        var path = Path_("old-words.json");
        ModesStore.Save(path, old);
        var r = ModesStore.Load(path, MaxBrightness);
        Assert.Null(r.Error);
        Assert.NotNull(r.Notice);
        Assert.Equal("modes.lid-words.json", r.Backup);
        Assert.Equal(Json(factory), Json(r.Value));

        // «Сеть, открыта, работа» next to an existing «Сеть, открыт, работа»: renaming would repeat a name, so nothing changes.
        var clash = Add(factory, Extra("Сеть, открыта, работа", new Conditions(Source: PowerSource.Ac, Lid: LidState.Open, Load: Workload.Work, Monitor: MonitorState.Connected)));
        Assert.Empty(ModesValidator.Errors(clash, MaxBrightness));
        ModesStore.Save(path, clash);
        r = ModesStore.Load(path, MaxBrightness);
        Assert.Null(r.Notice);
        Assert.Equal(Json(clash), Json(r.Value));

        Assert.Equal("Открытая крышка, закрыт", ProfileNames.WithCurrentLidWords("Открытая крышка, закрыта"));
    }
}
