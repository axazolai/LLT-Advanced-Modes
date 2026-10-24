using System.Collections.ObjectModel;
using LegionModes.Core;
using LegionModes.Core.Model;
using LegionModes.Core.Status;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers.GodMode;
using LenovoLegionToolkit.Lib.Features;

namespace LegionModes.Plugin.Apply;

// The plugin's GodMode presets live in LLT's godmode.json under the name "AM · <profile>". A preset whose name does not
// start with that prefix is never changed or removed.
public static class GodModePresets
{
    // godmode.json is read-modify-written: one writer at a time.
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    // The preset's own default (the hardware default carried by StepperValue); unchanged when it has none.
    static StepperValue? ToDefault(StepperValue? v) =>
        v is { } s && s.DefaultValue is { } d ? s.WithValue(d) : v;

    // The profile Id -> preset Guid map (preset-map.json in the plugin data dir); set once by the runtime.
    internal static string? MapPath { get; set; }

    // Persist only: no firmware write. Runs at start, at Take and after every config save, in or out of control. The presets
    // follow the profiles through the map (PresetPlanner): a renamed profile renames its preset, a profile that sets its own power
    // preset and has none gets one, and a mapped preset whose profile Id is gone from a config that loaded is deleted. A preset
    // without a mapping is never deleted, and a name without the "AM · " prefix is never changed.
    // prune: false while modes.json failed to load (the factory set stands in; the owner's presets stay).
    public static async Task SyncAsync(ModesConfig config, bool prune = true)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { await SyncCoreAsync(config, prune).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    static async Task SyncCoreAsync(ModesConfig config, bool prune)
    {
        var path = MapPath ?? throw new InvalidOperationException("the preset map path is not set");
        var gm = IoCContainer.Resolve<GodModeController>();
        var state = await gm.GetStateAsync().ConfigureAwait(false);
        var template = state.Presets[state.ActivePresetId];                       // carries StepperValue ranges and fan data
        var presets = new Dictionary<Guid, GodModePreset>(state.Presets);

        var (map, mapError) = PresetMapFile.Load(path);
        if (mapError is not null) Log.Error("preset-map.json", new InvalidOperationException(mapError));
        var plan = PresetPlanner.Plan(config, presets.ToDictionary(kv => kv.Key, kv => kv.Value.Name ?? ""), map,
            state.ActivePresetId, prune && mapError is null);

        foreach (var r in plan.Renames) presets[r.Id] = presets[r.Id] with { Name = r.NewName };
        foreach (var c in plan.Creates)
        {
            presets[c.NewId] = c.Source switch
            {
                { Kind: PresetSourceKind.Factory } s => FromPowerPreset(template, c.Name, FactoryProfiles.Presets()[s.FactoryName!]),
                { Kind: PresetSourceKind.CopyOf, CopyOf: { } from } when presets.TryGetValue(from, out var source) => source with { Name = c.Name },
                _ => template with { Name = c.Name, Overrides = new Dictionary<PowerOverrideKey, string>() },
            };
        }
        foreach (var d in plan.Deletes)
        {
            Log.Trace($"preset {presets[d].Name} removed: its profile is gone");
            presets.Remove(d);
        }
        if (plan.ChangesPresets)
        {
            await gm.SetStateAsync(new GodModeState
            {
                ActivePresetId = state.ActivePresetId,
                Presets = new ReadOnlyDictionary<Guid, GodModePreset>(presets),
            }).ConfigureAwait(false);   // persist only
        }
        if (mapError is null && !SameMap(map, plan.Map)) PresetMapFile.Save(path, plan.Map);
    }

    // Factory reset: every "AM · <name>" preset whose name is in the factory set gets the factory values again, in place
    // (persist only; the next full apply activates them). Run after modes.json was replaced and the presets synced.
    public static async Task ResetToFactoryAsync(ModesConfig config)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var gm = IoCContainer.Resolve<GodModeController>();
            var state = await gm.GetStateAsync().ConfigureAwait(false);
            var factory = FactoryProfiles.Presets();
            var presets = new Dictionary<Guid, GodModePreset>(state.Presets);
            var changed = false;
            foreach (var profile in config.Profiles)
            {
                if (!factory.TryGetValue(profile.Name, out var values) || FindPresetId(state, profile) is not { } id) continue;
                var p = state.Presets[id];
                presets[id] = FromPowerPreset(p, p.Name ?? ProfileNames.PresetLabel(profile), values);
                changed = true;
            }
            if (changed)
                await gm.SetStateAsync(new GodModeState
                {
                    ActivePresetId = state.ActivePresetId,
                    Presets = new ReadOnlyDictionary<Guid, GodModePreset>(presets),
                }).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    static bool SameMap(IReadOnlyDictionary<Guid, Guid> a, IReadOnlyDictionary<Guid, Guid> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    static GodModePreset FromPowerPreset(GodModePreset template, string name, PowerPreset pp) => template with
    {
        Name = name,
        CPULongTermPowerLimit = template.CPULongTermPowerLimit?.WithValue(pp.CpuLongW),
        CPUShortTermPowerLimit = template.CPUShortTermPowerLimit?.WithValue(pp.CpuShortW),
        CPUPeakPowerLimit = template.CPUPeakPowerLimit?.WithValue(pp.CpuPeakW),
        CPUCrossLoadingPowerLimit = template.CPUCrossLoadingPowerLimit?.WithValue(pp.CpuCrossW),
        CPUTemperatureLimit = template.CPUTemperatureLimit?.WithValue(pp.CpuTempC),
        GPUPowerBoost = template.GPUPowerBoost?.WithValue(pp.DynamicBoostW),
        GPUConfigurableTGP = template.GPUConfigurableTGP?.WithValue(pp.ConfigurableTgpW),
        GPUTemperatureLimit = template.GPUTemperatureLimit?.WithValue(pp.GpuTempC),
        GPUTotalProcessingPowerTargetOnAcOffsetFromBaseline =
            template.GPUTotalProcessingPowerTargetOnAcOffsetFromBaseline?.WithValue(pp.TppW),
        FanTableInfo = template.FanTableInfo is { } fti
            ? new FanTableInfo(fti.Data, new FanTable(pp.FanCurve.Select(v => (ushort)v).ToArray())) : null,
        FanFullSpeed = pp.FanFullSpeed,
        MinValueOffset = pp.MinValueOffset,
        MaxValueOffset = pp.MaxValueOffset,
        EnableOverclocking = false,
        AllCoreCurveOptimizer = template.AllCoreCurveOptimizer?.WithValue(pp.CurveOptimizer),
        EnableAllCoreCurveOptimizer = pp.CurveOptimizer != 0,
        // Values the profile does not manage are the same on every plugin preset, whatever the template holds.
        CPUPL2Tau = ToDefault(template.CPUPL2Tau),
        APUsPPTPowerLimit = ToDefault(template.APUsPPTPowerLimit),
        GPUToCPUDynamicBoost = ToDefault(template.GPUToCPUDynamicBoost),
        PrecisionBoostOverdriveScaler = ToDefault(template.PrecisionBoostOverdriveScaler),
        PrecisionBoostOverdriveBoostFrequency = ToDefault(template.PrecisionBoostOverdriveBoostFrequency),
        Overrides = new Dictionary<PowerOverrideKey, string>(),
    };

    // Makes the profile's preset the active one and puts the machine in GodMode with it applied.
    public static async Task ActivateAsync(Profile profile)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { await ActivateCoreAsync(profile).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    // The profile's preset: through the profile -> preset map, else a preset whose label carries one of the profile's names.
    internal static Guid? FindPresetId(GodModeState state, Profile profile)
    {
        if (MapPath is { } path)
        {
            var (map, _) = PresetMapFile.Load(path);
            if (map.TryGetValue(profile.Id, out var mapped) && state.Presets.TryGetValue(mapped, out var m) && Paths.IsOurs(m.Name)) return mapped;
        }
        var names = ProfileNames.AllNames(profile);
        return state.Presets.Where(p => Paths.IsOurs(p.Value.Name) && names.Contains(Paths.PresetText(p.Value.Name!)))
            .Select(p => (Guid?)p.Key).FirstOrDefault();
    }

    static async Task ActivateCoreAsync(Profile profile)
    {
        var gm = IoCContainer.Resolve<GodModeController>();
        var state = await gm.GetStateAsync().ConfigureAwait(false);
        var id = FindPresetId(state, profile)
                 ?? throw new InvalidOperationException($"preset {ProfileNames.PresetLabel(profile)} not found");
        await gm.SetStateAsync(state with { ActivePresetId = id }).ConfigureAwait(false);
        var power = IoCContainer.Resolve<PowerModeFeature>();
        if (await power.GetStateAsync().ConfigureAwait(false) != PowerModeState.GodMode)
            await power.SetStateAsync(PowerModeState.GodMode).ConfigureAwait(false);   // applies via PowerModeListener
        else
            await ApplyOrThrowAsync(gm).ConfigureAwait(false);
    }

    // GodMode is not applied while Vantage, Legion Space or Legion Zone is on.
    static async Task ApplyOrThrowAsync(GodModeController gm)
    {
        var (applied, _) = await gm.ApplyStateWithResultAsync().ConfigureAwait(false);
        if (!applied) throw new InvalidOperationException(Tr.T("GodMode not applied: Vantage / Legion Space / Legion Zone are on"));
    }

    // The active preset and the power mode before the Profiles tab opens LLT's window.
    public sealed record Capture(Guid ActivePresetId, PowerModeState Mode);

    public static async Task<Capture> CaptureAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var gm = IoCContainer.Resolve<GodModeController>();
            var state = await gm.GetStateAsync().ConfigureAwait(false);
            var mode = await IoCContainer.Resolve<PowerModeFeature>().GetStateAsync().ConfigureAwait(false);
            return new Capture(state.ActivePresetId, mode);
        }
        finally { Gate.Release(); }
    }

    public static async Task RestoreAsync(Capture c)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var gm = IoCContainer.Resolve<GodModeController>();
            var state = await gm.GetStateAsync().ConfigureAwait(false);
            if (state.ActivePresetId != c.ActivePresetId && state.Presets.ContainsKey(c.ActivePresetId))
                await gm.SetStateAsync(state with { ActivePresetId = c.ActivePresetId }).ConfigureAwait(false);
            var power = IoCContainer.Resolve<PowerModeFeature>();
            if (await power.GetStateAsync().ConfigureAwait(false) != c.Mode)
                await power.SetStateAsync(c.Mode).ConfigureAwait(false);
            else if (c.Mode == PowerModeState.GodMode)
                await ApplyOrThrowAsync(gm).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    internal static async Task<bool> ExistsAsync(Profile profile)
    {
        var gm = IoCContainer.Resolve<GodModeController>();
        var state = await gm.GetStateAsync().ConfigureAwait(false);
        return FindPresetId(state, profile) is not null;
    }
}
