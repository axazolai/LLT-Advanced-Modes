using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Plugin.Backlight;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Features.PanelLogo;

namespace LegionModes.Plugin.Apply;

// Keyboard, logo and ports lighting follow GUID_CONSOLE_DISPLAY_STATE: dimmed or off -> everything off (the values are
// remembered first); on -> the remembered values back, or the in-force profile's lighting when none were remembered. The
// decisions come from IdleLightingPolicy; nothing happens without control or while paused.
public sealed class IdleLighting(Func<bool> active, Func<Lighting?> current)
{
    sealed record Remembered(int? Keyboard, PanelLogoBacklightState? Logo, PortsBacklightState? Ports);

    readonly SemaphoreSlim _gate = new(1, 1);
    DisplayStateKind _previous = DisplayStateKind.On;
    volatile bool _switchedOff;
    Remembered? _remembered;
    Lighting? _deferred;          // a lighting applied while everything is off: it replaces the remembered keyboard values at restore

    public bool SwitchedOff => _switchedOff;

    // Called by the applier's lighting step. true = the lights are off with the display: the value is kept for the restore.
    public bool Defer(Lighting l)
    {
        if (!_switchedOff) return false;
        _deferred = l;
        return true;
    }

    // Raised on the UI dispatcher: the work goes to the pool and never throws.
    public void OnDisplayState(DisplayStateKind now) => _ = Task.Run(() => HandleAsync(now));

    internal async Task HandleAsync(DisplayStateKind now)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = _previous;
            _previous = now;
            var action = IdleLightingPolicy.Next(previous, now, active(), _switchedOff);
            if (action == LightingAction.SwitchOff) await SwitchOffAsync().ConfigureAwait(false);
            else if (action == LightingAction.Restore) await RestoreAsync().ConfigureAwait(false);
            // Control lost or paused while off: the lights stay as they are; LLT or the next apply owns them.
            else if (_switchedOff && !active()) { _switchedOff = false; _remembered = null; _deferred = null; }
        }
        catch (Exception e) { Log.Error("idle lighting", e); }
        finally { _gate.Release(); }
    }

    async Task SwitchOffAsync()
    {
        int? keyboard = null;
        PanelLogoBacklightState? logo = null;
        PortsBacklightState? ports = null;
        var spectrum = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();
        var logoFeature = IoCContainer.Resolve<PanelLogoBacklightFeature>();
        var portsFeature = IoCContainer.Resolve<PortsBacklightFeature>();

        // Remember first, then switch off; each part is independent.
        try
        {
            if (await spectrum.IsSupportedAsync().ConfigureAwait(false))
            {
                keyboard = await spectrum.GetBrightnessAsync().ConfigureAwait(false);
                await spectrum.SetBrightnessAsync(0).ConfigureAwait(false);
            }
        }
        catch (Exception e) { Log.Error("idle lighting: keyboard off", e); }
        try
        {
            if (await logoFeature.IsSupportedAsync().ConfigureAwait(false))
            {
                logo = await logoFeature.GetStateAsync().ConfigureAwait(false);
                await logoFeature.SetStateAsync(PanelLogoBacklightState.Off).ConfigureAwait(false);
            }
        }
        catch (Exception e) { Log.Error("idle lighting: logo off", e); }
        try
        {
            if (await portsFeature.IsSupportedAsync().ConfigureAwait(false))
            {
                ports = await portsFeature.GetStateAsync().ConfigureAwait(false);
                await portsFeature.SetStateAsync(PortsBacklightState.Off).ConfigureAwait(false);
            }
        }
        catch (Exception e) { Log.Error("idle lighting: ports off", e); }

        _remembered = keyboard is null && logo is null && ports is null ? null : new Remembered(keyboard, logo, ports);
        _deferred = null;
        _switchedOff = true;
        Log.Trace("idle lighting: off");
    }

    async Task RestoreAsync()
    {
        var remembered = _remembered;
        var deferred = _deferred;
        _switchedOff = false;
        _remembered = null;
        _deferred = null;

        if (remembered is null)
        {
            // Nothing was remembered: the in-force profile's lighting.
            var profileLighting = deferred ?? current();
            if (profileLighting is not null) await LightingApplier.ApplyAsync(profileLighting).ConfigureAwait(false);
            Log.Trace("idle lighting: restored from the profile");
            return;
        }

        // A lighting applied while off wins over the remembered keyboard values.
        if (deferred is { } d)
        {
            try { await LightingApplier.ApplyAsync(d).ConfigureAwait(false); }
            catch (Exception e) { Log.Error("idle lighting: deferred keyboard", e); }
        }
        else if (remembered.Keyboard is { } brightness)
        {
            try { await IoCContainer.Resolve<SpectrumKeyboardBacklightController>().SetBrightnessAsync(brightness).ConfigureAwait(false); }
            catch (Exception e) { Log.Error("idle lighting: keyboard restore", e); }
        }
        if (remembered.Logo is { } logo)
        {
            try { await IoCContainer.Resolve<PanelLogoBacklightFeature>().SetStateAsync(logo).ConfigureAwait(false); }
            catch (Exception e) { Log.Error("idle lighting: logo restore", e); }
        }
        if (remembered.Ports is { } ports)
        {
            try { await IoCContainer.Resolve<PortsBacklightFeature>().SetStateAsync(ports).ConfigureAwait(false); }
            catch (Exception e) { Log.Error("idle lighting: ports restore", e); }
        }
        Log.Trace("idle lighting: restored");
    }
}
