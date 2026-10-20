using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers.GodMode;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Features.Hybrid.Notify;
using LenovoLegionToolkit.Lib.System.Management;

namespace LegionModes.Plugin.Apply;

// Parameter 1, power: the firmware mode, or GodMode with the profile's own preset "AM · <source profile>". Every write is
// read back: the power mode, the fan table, and the GPU values (a GPU value can be refused silently while the dGPU is absent
// or just attached).
internal sealed class PowerApplier(Func<ModesConfig> modes)
{
    // LLT reads and writes a capability at (id & mask) on every platform (GodModePlatformConfiguration.CapabilityIdMask).
    const uint CapabilityMask = 0xFFFF00FF;

    readonly SemaphoreSlim _lock = new(1, 1);
    Profile? _presetProfile;   // the profile whose own preset was applied last; null after a firmware mode

    // The GPU values the preset carries, read back after every apply and every dGPU attach.
    static readonly (CapabilityID Id, string Label, Func<GodModePreset, int?> Want)[] GpuValues =
    [
        (CapabilityID.GPUConfigurableTGP, "Configurable TGP", p => p.GPUConfigurableTGP?.Value),
        (CapabilityID.GPUPowerBoost, "Dynamic Boost", p => p.GPUPowerBoost?.Value),
        (CapabilityID.GPUTotalProcessingPowerTargetOnAcOffsetFromBaseline, "TPP",
            p => p.GPUTotalProcessingPowerTargetOnAcOffsetFromBaseline?.Value),
    ];

    // The one place that converts between a preset value (watts) and the firmware's reading. LLT's own reader
    // (GodModeController.LoadStateFromStoreAsync) stores GetFeatureValueAsync(id & mask) straight into the StepperValue, so
    // the firmware's encoding is the preset's: watts, no conversion.
    internal static int ToFirmware(int presetValue) => presetValue;
    internal static int FromFirmware(int raw) => raw;

    static uint Id(CapabilityID id) => (uint)id & CapabilityMask;

    // forceTpp: write TPP again even when it reads back right (LLT start: the first write can land before the dGPU is up).
    public async Task<string?> ApplyAsync(Sourced<PowerSetting> power, bool forceTpp)
    {
        if (power.Value.Firmware is { } firmware) return await ApplyFirmwareModeAsync(firmware).ConfigureAwait(false);

        var source = power.Source;
        if (!await GodModePresets.ExistsAsync(source).ConfigureAwait(false))
            await GodModePresets.SyncAsync(modes(), prune: false).ConfigureAwait(false);   // presets are created at start; a late profile gets one here
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await GodModePresets.Gate.WaitAsync().ConfigureAwait(false);
            try { return await ApplyOwnPresetAsync(source, forceTpp).ConfigureAwait(false); }
            finally { GodModePresets.Gate.Release(); }
        }
        finally { _lock.Release(); }
    }

    async Task<string?> ApplyFirmwareModeAsync(FirmwareMode firmware)
    {
        var target = firmware switch
        {
            FirmwareMode.Quiet => PowerModeState.Quiet,
            FirmwareMode.Performance => PowerModeState.Performance,
            _ => PowerModeState.Balance,
        };
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _presetProfile = null;
            var feature = IoCContainer.Resolve<PowerModeFeature>();
            if (await feature.GetStateAsync().ConfigureAwait(false) != target)
                await feature.SetStateAsync(target).ConfigureAwait(false);
            var now = await feature.GetStateAsync().ConfigureAwait(false);
            return now == target ? null : Tr.F("режим питания: стоит {0}, ожидался {1}", now, target);
        }
        finally { _lock.Release(); }
    }

    async Task<string?> ApplyOwnPresetAsync(Profile profile, bool forceTpp)
    {
        var gm = IoCContainer.Resolve<GodModeController>();
        var state = await gm.GetStateAsync().ConfigureAwait(false);
        if (GodModePresets.FindPresetId(state, profile) is not { } id) return Tr.F("пресет GodMode {0} не найден", ProfileNames.PresetLabel(profile));
        _presetProfile = profile;

        var power = IoCContainer.Resolve<PowerModeFeature>();
        if (await power.GetStateAsync().ConfigureAwait(false) != PowerModeState.GodMode)
            await power.SetStateAsync(PowerModeState.GodMode, applyGodModePreset: false).ConfigureAwait(false);
        var (applied, software) = await gm.ApplyStateWithResultAsync(state with { ActivePresetId = id }).ConfigureAwait(false);
        if (!applied) return Tr.F("GodMode не применён: {0}", Describe(software));   // Vantage / Legion Space / Legion Zone enabled

        var problems = new List<string>();
        var mode = await power.GetStateAsync().ConfigureAwait(false);
        if (mode != PowerModeState.GodMode) problems.Add(Tr.F("режим питания: стоит {0}, ожидался GodMode", mode));

        var preset = state.Presets[id];
        if (preset.FanTableInfo is { } fan)
        {
            try
            {
                if (!await gm.SetFanTableWithVerifyAsync(fan.Table).ConfigureAwait(false))
                    problems.Add(Tr.T("таблица вентиляторов не совпала после записи"));
            }
            catch (Exception e) { Log.Error("fan table verify", e); problems.Add(Tr.F("таблица вентиляторов: {0}", e.Message)); }
        }

        if (await DGpuReadyAsync().ConfigureAwait(false))
            problems.AddRange(await VerifyGpuValuesAsync(preset, forceTpp).ConfigureAwait(false));
        else Log.Trace("GPU values not read back: the dGPU is not attached (checked again on attach)");
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    static string Describe(object software) => software?.ToString() is { Length: > 0 } s
        ? Tr.F("{0} (Vantage / Legion Space / Legion Zone включены)", s) : Tr.T("Vantage / Legion Space / Legion Zone включены");

    // The preset's GPU values after a dGPU attach. null = fine or nothing to check.
    public async Task<string?> VerifyAfterAttachAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_presetProfile is not { } profile) return null;
            await GodModePresets.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var state = await IoCContainer.Resolve<GodModeController>().GetStateAsync().ConfigureAwait(false);
                var preset = GodModePresets.FindPresetId(state, profile) is { } found ? (GodModePreset?)state.Presets[found] : null;
                if (preset is not { } p) return null;
                var problems = await VerifyGpuValuesAsync(p, forceTpp: false).ConfigureAwait(false);
                return problems.Count == 0 ? null : string.Join("; ", problems);
            }
            finally { GodModePresets.Gate.Release(); }
        }
        finally { _lock.Release(); }
    }

    // Reads each GPU value, writes it once more on a mismatch, reads again; a second mismatch is a problem.
    static async Task<List<string>> VerifyGpuValuesAsync(GodModePreset preset, bool forceTpp)
    {
        var problems = new List<string>();
        foreach (var (cap, label, want) in GpuValues)
        {
            if (want(preset) is not { } wanted) continue;
            var expected = ToFirmware(wanted);
            try
            {
                var force = forceTpp && cap == CapabilityID.GPUTotalProcessingPowerTargetOnAcOffsetFromBaseline;
                if (!force && await WMI.LenovoOtherMethod.GetFeatureValueAsync(Id(cap)).ConfigureAwait(false) == expected) continue;
                await WMI.LenovoOtherMethod.SetFeatureValueAsync(Id(cap), expected).ConfigureAwait(false);
                var after = await WMI.LenovoOtherMethod.GetFeatureValueAsync(Id(cap)).ConfigureAwait(false);
                if (after != expected)
                    problems.Add(Tr.F("{0}: записано {1}, прочитано {2}", label, wanted, FromFirmware(after)));
            }
            catch (Exception e)
            {
                Log.Error($"GPU value {label}", e);
                problems.Add($"{label}: {e.Message}");
            }
        }
        return problems;
    }

    // GPU values can be written only while the dGPU is attached. A machine that cannot say counts as ready.
    internal static async Task<bool> DGpuReadyAsync()
    {
        try
        {
            var notify = IoCContainer.Resolve<DGPUNotify>();
            if (!await notify.IsSupportedAsync().ConfigureAwait(false)) return true;
            return await notify.IsDGPUAvailableAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error("dGPU availability", e);
            return true;
        }
    }
}
