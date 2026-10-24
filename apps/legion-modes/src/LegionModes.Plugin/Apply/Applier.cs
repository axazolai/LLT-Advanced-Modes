using LegionModes.Core;
using System.IO;
using System.Diagnostics;
using LegionModes.Core.Engine;
using LegionModes.Core.Ipc;
using LegionModes.Core.Model;
using LegionModes.Plugin.Backlight;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Features.Hybrid;
using LenovoLegionToolkit.Lib.Features.Hybrid.Notify;

namespace LegionModes.Plugin.Apply;

// Writes the decision's parameters through LLT's own controllers (LLT stays the only firmware writer) and the Windows
// APIs. Each parameter fails on its own; the result holds an entry for every parameter it was asked to apply
// (null = applied and read back, text = error). Every step runs even when an earlier one failed.
//   controlScheme: the Windows scheme stored at takeover; modes: the live config; note: the status line for a held dGPU;
//   problem: a fault found outside an apply (the GPU values after a dGPU attach); lightingActive: control and not paused.
public sealed class Applier(Func<Guid?> controlScheme, Func<ModesConfig> modes, Action<string?> note,
    Action<string> problem, Func<bool> lightingActive) : IApplier
{
    readonly PowerApplier _power = new(modes);
    readonly SemaphoreSlim _idleGate = new(1, 1);
    bool _powerApplied;                // TPP is written again on the first own-preset apply after the plugin started
    Lighting? _lighting;               // the lighting in force: what idle lighting restores when it remembered nothing
    DGPUNotify? _notify;
    EventHandler<bool>? _attached;

    // The idle lighting follows Runtime.DisplayStateChanged.
    Lazy<IdleLighting>? _idle;
    public IdleLighting Idle => (_idle ??= new Lazy<IdleLighting>(() => new IdleLighting(lightingActive, () => _lighting))).Value;

    // Hardware session start: the dGPU attach hook. Safe to call once; a failure leaves the hook out.
    public void Start()
    {
        try
        {
            _notify = IoCContainer.Resolve<DGPUNotify>();
            _attached = (_, attached) => { if (attached) _ = Task.Run(VerifyAfterAttachAsync); };
            _notify.Notified += _attached;
        }
        catch (Exception e) { Log.Error("dGPU attach hook", e); _notify = null; }
    }

    public void Stop()
    {
        try { if (_notify is not null && _attached is not null) _notify.Notified -= _attached; }
        catch (Exception e) { Log.Error("dGPU attach hook removal", e); }
        _notify = null;
    }

    // The driver needs a moment after the device arrives before the GPU values are writable.
    async Task VerifyAfterAttachAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var text = await _power.VerifyAfterAttachAsync().ConfigureAwait(false);
            if (text is not null) problem(Tr.F("GPU values after dGPU connected: {0}", text));
            else Log.Trace("GPU values verified after dGPU attach");
        }
        catch (Exception e) { Log.Error("verify after dGPU attach", e); }
    }

    public async Task<IReadOnlyDictionary<ParameterId, ParameterResult>> ApplyAsync(Decision d, IReadOnlyList<ParameterId> changed)
    {
        var result = new Dictionary<ParameterId, ParameterResult>();
        var e = d.Parameters;
        _lighting = e.Lighting.Value;
        // Spec section 4 order: GPU mode, power, processor, charging and USB, panel and external, timers, lighting.
        foreach (var parameter in Order.Where(changed.Contains))
        {
            try
            {
                result[parameter] = parameter switch
                {
                    ParameterId.Gpu => ParameterResult.Of(await ApplyGpuModeAsync(d, changed.Contains(ParameterId.Power) && e.Power.Value.Firmware is null).ConfigureAwait(false)),
                    ParameterId.Power => ParameterResult.Of(await ApplyPowerAsync(e.Power).ConfigureAwait(false)),
                    ParameterId.Processor => ParameterResult.Of(ApplyProcessor(e.Processor.Value)),
                    ParameterId.Charging => ParameterResult.Of(await ApplyChargingAsync(e.Charging.Value).ConfigureAwait(false)),
                    ParameterId.Usb => ParameterResult.Of(await ApplyUsbAsync(e.Usb.Value).ConfigureAwait(false)),
                    ParameterId.Panel => await Displays.ApplyPanelAsync(e.Panel.Value).ConfigureAwait(false),
                    ParameterId.External => ParameterResult.Of(await Displays.ApplyExternalAsync(e.External.Value).ConfigureAwait(false)),
                    ParameterId.Timers => ParameterResult.Of(await ApplyTimersAsync(e.Timers.Value).ConfigureAwait(false)),
                    ParameterId.Lighting => ParameterResult.Of(await ApplyLightingAsync(e.Lighting.Value).ConfigureAwait(false)),
                    _ => ParameterResult.Ok,
                };
            }
            catch (Exception ex)
            {
                Log.Error($"apply {parameter}", ex);
                result[parameter] = Thrown(ex);
            }
        }
        // A projection change can move the panel to another mode: the panel rate is set again after the external step.
        if (changed.Contains(ParameterId.External) && e.External.Value.Projection != Projection.DoNotTouch
            && result.TryGetValue(ParameterId.External, out var extResult) && !extResult.Failed)
        {
            ParameterResult again;
            try { again = await Displays.ApplyPanelAsync(e.Panel.Value).ConfigureAwait(false); }
            catch (Exception ex) { Log.Error("panel rate after projection", ex); again = Thrown(ex); }
            if (changed.Contains(ParameterId.Panel)) result[ParameterId.Panel] = again;
            else if (again.Failed) result[ParameterId.External] = new ParameterResult(again.Kind, "panel rate after projection: " + again.Text);
        }
        // A parameter this class does not know is still answered.
        foreach (var parameter in changed) result.TryAdd(parameter, ParameterResult.Ok);
        return result;
    }

    // A result the system gave on purpose is an error; any other exception is a fault.
    static ParameterResult Thrown(Exception ex) =>
        ex is ApplyResultException ? ParameterResult.Of(ex.Message) : ParameterResult.Fault(ex.GetType().Name + ": " + ex.Message);

    static readonly ParameterId[] Order =
    [
        ParameterId.Gpu, ParameterId.Power, ParameterId.Processor, ParameterId.Charging, ParameterId.Usb,
        ParameterId.Panel, ParameterId.External, ParameterId.Timers, ParameterId.Lighting,
    ];

    public async Task<string?> SleepNowAsync()
    {
        var error = await SystemSleep.SuspendAsync().ConfigureAwait(false);
        if (error is not null) Log.Error("sleep now", new InvalidOperationException(error));
        return error;
    }

    // ---- GPU mode -----------------------------------------------------------------------------------------------

    internal const string DGpuHeld = "dGPU (held by the hardware)";

    async Task<string?> ApplyGpuModeAsync(Decision d, bool dGpuNeeded)
    {
        if (d.DGpu) { Log.Trace("GPU mode skipped: dGPU override holds"); return null; }
        var feature = IoCContainer.Resolve<HybridModeFeature>();
        if (!await feature.IsSupportedAsync().ConfigureAwait(false)) { Log.Trace("GPU mode skipped: not supported"); return null; }
        // The hardware is in discrete-only mode while the engine has no override (the override is gone after the restart
        // it needed): that mode is never switched here.
        if (await feature.GetStateAsync().ConfigureAwait(false) == HybridModeState.Off)
        {
            Log.Trace("GPU mode skipped: " + DGpuHeld);
            note(DGpuHeld);
            return null;
        }
        note(null);
        var target = d.Parameters.Gpu.Value switch
        {
            GpuMode.Hybrid => HybridModeState.On,
            GpuMode.HybridIgpuOnly => HybridModeState.OnIGPUOnly,
            _ => HybridModeState.OnAuto,
        };
        if (!(await feature.GetAllStatesAsync().ConfigureAwait(false)).Contains(target))
            return $"GPU mode {target} is not available on this machine";
        if (await feature.GetStateAsync().ConfigureAwait(false) == target)
        {
            if (target == HybridModeState.OnIGPUOnly) await EjectDGpuAsync(feature).ConfigureAwait(false);
            return dGpuNeeded && target != HybridModeState.OnIGPUOnly ? await WaitForDGpuAsync().ConfigureAwait(false) : null;
        }
        try
        {
            await feature.SetStateAsync(target).ConfigureAwait(false);
            if (target == HybridModeState.OnIGPUOnly) await EjectDGpuAsync(feature).ConfigureAwait(false);
        }
        catch (Exception e) when (target == HybridModeState.OnIGPUOnly)
        {
            // iGPU-only can fail while a dGPU application runs: Hybrid stays.
            Log.Error("iGPU-only", e);
            try { await feature.SetStateAsync(HybridModeState.On).ConfigureAwait(false); }
            catch (Exception e2) { Log.Error("fall back to Hybrid", e2); return $"iGPU-only failed: {e.Message}; fall back to Hybrid failed: {e2.Message}"; }
            return Tr.F("iGPU-only refused (stayed on Hybrid): {0}", e.Message);
        }
        // The power step that follows writes GPU values: they need the dGPU, so wait for it to attach.
        return dGpuNeeded && target != HybridModeState.OnIGPUOnly ? await WaitForDGpuAsync().ConfigureAwait(false) : null;
    }

    // The mode write alone leaves the card attached (seen on the laptop 2026-10-24: the CLI and the plugin set iGPU-only, the card stayed
    // for minutes; LLT's own control releases it at once). LLT's control calls this after the write: it ejects the card when nothing holds it.
    static async Task EjectDGpuAsync(HybridModeFeature feature)
    {
        // The keeper's open NVIDIA library would hold the card (measured 2026-10-24: gone 15 s after the keeper stopped, still there 13 min with it).
        try
        {
            await PipeClient.SendAsync(Paths.KeeperPipe, new ReleaseGpu(90), TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            await Task.Delay(1000).ConfigureAwait(false);
        }
        catch (Exception e) { Log.Error("keeper release", e); }
        try { await feature.EnsureDGPUEjectedIfNeededAsync().ConfigureAwait(false); Log.Trace("iGPU-only: dGPU eject requested"); }
        catch (Exception e) { Log.Error("dGPU eject", e); }
    }

    static readonly TimeSpan DGpuWait = TimeSpan.FromSeconds(10);

    // Up to 10 s for the dGPU after a switch to Hybrid; the GPU values are read back again on the attach event either way.
    static async Task<string?> WaitForDGpuAsync()
    {
        try
        {
            var notify = IoCContainer.Resolve<DGPUNotify>();
            if (!await notify.IsSupportedAsync().ConfigureAwait(false)) return null;
            var deadline = DateTime.UtcNow + DGpuWait;
            while (true)
            {
                if (await notify.IsDGPUAvailableAsync().ConfigureAwait(false)) return null;
                if (DateTime.UtcNow >= deadline) return Tr.T("dGPU did not connect within 10 s");
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Log.Error("wait for dGPU", e);
            return null;
        }
    }

    // The dGPU override path. The UI calls it after its own confirmation dialog: it sets the discrete-only mode and
    // restarts the machine. Returns an error text, or null once the restart has been requested. The applier never calls
    // it by itself.
    public static async Task<string?> ApplyDGpuOverrideAndRestartAsync()
    {
        try
        {
            var feature = IoCContainer.Resolve<HybridModeFeature>();
            if (!(await feature.GetAllStatesAsync().ConfigureAwait(false)).Contains(HybridModeState.Off))
                return "discrete-only GPU mode is not available on this machine";
            await feature.SetStateAsync(HybridModeState.Off).ConfigureAwait(false);
            using var shutdown = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return null;
        }
        catch (Exception e)
        {
            Log.Error("dGPU override", e);
            return e.Message;
        }
    }

    // ---- power --------------------------------------------------------------------------------------------------

    async Task<string?> ApplyPowerAsync(Sourced<PowerSetting> power)
    {
        var first = !_powerApplied;
        var error = await _power.ApplyAsync(power, forceTpp: first).ConfigureAwait(false);
        if (power.Value.Firmware is null) _powerApplied = true;
        return error;
    }

    string? ApplyProcessor(ProcessorScheme p)
    {
        return ProcessorApplier.Apply(controlScheme(), p);   // throws when a write does not read back
    }

    // ---- charging, USB ------------------------------------------------------------------------------------------

    static async Task<string?> ApplyChargingAsync(ChargingMode mode)
    {
        if (mode == ChargingMode.DoNotTouch) { Log.Trace("charging skipped: do not touch"); return null; }
        var feature = IoCContainer.Resolve<BatteryFeature>();
        if (!await feature.IsSupportedAsync().ConfigureAwait(false)) { Log.Trace("charging skipped: not supported"); return null; }
        var target = mode switch
        {
            ChargingMode.Conservation => BatteryState.Conservation,
            ChargingMode.Rapid => BatteryState.RapidCharge,
            _ => BatteryState.Normal,
        };
        await feature.SetStateAsync(target).ConfigureAwait(false);
        return null;
    }

    static async Task<string?> ApplyUsbAsync(UsbPower usb)
    {
        var feature = IoCContainer.Resolve<AlwaysOnUSBFeature>();
        if (!await feature.IsSupportedAsync().ConfigureAwait(false)) { Log.Trace("always-on USB skipped: not supported"); return null; }
        var target = usb switch
        {
            UsbPower.Always => AlwaysOnUSBState.OnAlways,
            UsbPower.WhenSleeping => AlwaysOnUSBState.OnWhenSleeping,
            _ => AlwaysOnUSBState.Off,
        };
        await feature.SetStateAsync(target).ConfigureAwait(false);
        return null;
    }

    // ---- idle timers --------------------------------------------------------------------------------------------

    async Task<string?> ApplyTimersAsync(IdleTimers t)
    {
        var values = WindowsTimers.From(t);
        WindowsPower.ApplyTimers(controlScheme(), values);
        if (values.StaticBrightness is { } percent)
            await IoCContainer.Resolve<DisplayBrightnessController>().SetBrightnessAsync(percent).ConfigureAwait(false);
        return null;
    }

    // ---- lighting -----------------------------------------------------------------------------------------------

    // While the idle lighting holds the lights off, the new lighting waits for the display to come back.
    async Task<string?> ApplyLightingAsync(Lighting l)
    {
        if (Idle.Defer(l)) { Log.Trace("lighting deferred: switched off with the display"); return null; }
        return await LightingApplier.ApplyAsync(l).ConfigureAwait(false);
    }
}
