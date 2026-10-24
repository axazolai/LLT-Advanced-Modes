using LegionModes.Core;
using System.Runtime.InteropServices;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.System;
using WindowsDisplayAPI;
using WindowsDisplayAPI.DisplayConfig;
using WindowsDisplayAPI.Native.DeviceContext;

namespace LegionModes.Plugin.Apply;

// Panel refresh rate (LLT's RefreshRateFeature), projection mode and the refresh rate of external displays.
internal static class Displays
{
    const uint SdcTopologyInternal = 0x1;
    const uint SdcTopologyClone = 0x2;
    const uint SdcTopologyExtend = 0x4;
    const uint SdcTopologyExternal = 0x8;
    const uint SdcApply = 0x80;

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

    // The exact rate, else the nearest lower one, else (nothing lower) the lowest available. Rates need not be sorted.
    internal static int? Pick(IEnumerable<int> available, int wanted)
    {
        var all = available.Distinct().OrderBy(x => x).ToArray();
        if (all.Length == 0) return null;
        if (all.Contains(wanted)) return wanted;
        var lower = all.Where(x => x < wanted).ToArray();
        return lower.Length > 0 ? lower[^1] : all[0];
    }

    // Drr is 120 Hz physical whatever Hz holds (the factory root carries Hz 0).
    internal const int DrrPhysicalHz = 120;

    // The expected state after a panel apply: the physical (signal) rate, the virtual rate Windows reports, the DRR flag.
    internal readonly record struct PanelExpectation(int PhysicalHz, int VirtualHz, bool Drr);

    // What the internal display path reports now. null = no internal display.
    internal readonly record struct PanelReading(int PhysicalHz, int VirtualHz, bool Drr);

    // The exact rate, else the nearest lower one (Pick). null = ok or skipped (no internal display); text = a mismatch.
    public static async Task<string?> ApplyPanelRateAsync(int hz)
    {
        var feature = IoCContainer.Resolve<RefreshRateFeature>();
        var states = await feature.GetAllStatesAsync().ConfigureAwait(false);
        if (Pick(states.Where(s => !s.IsDynamic).Select(s => s.Frequency), hz) is not { } rate)
        {
            Log.Trace("panel rate skipped: no refresh rates reported (no internal display)");
            return null;
        }
        // A rate that already stands is not set again: every set is a display mode change.
        if (!(await feature.GetStateAsync().ConfigureAwait(false)).Equals(new RefreshRate(rate)))
            await feature.SetStateAsync(new RefreshRate(rate)).ConfigureAwait(false);
        return await VerifyPanelAsync(new PanelExpectation(rate, rate, false)).ConfigureAwait(false);
    }

    // Fixed: a true signal mode at the rate; Drr: the dynamic (DRR) state at 120 Hz physical, else a fixed 120 Hz, which is a
    // note (DRR is not there to be had), not an error. Both are read back from the display path.
    public static async Task<ParameterResult> ApplyPanelAsync(PanelRate rate)
    {
        switch (rate.Kind)
        {
            case PanelRateKind.Fixed:
                return ParameterResult.Of(await ApplyPanelRateAsync(rate.Hz).ConfigureAwait(false));
            case PanelRateKind.Drr:
                var feature = IoCContainer.Resolve<RefreshRateFeature>();
                var states = await feature.GetAllStatesAsync().ConfigureAwait(false);
                // LLT reports the dynamic state at the panel's maximum rate with the low rate as its base (a 240 Hz panel: 240 / base),
                // not at 120 Hz; the 120 Hz state is preferred where a panel does offer one.
                var dynamic = states.Where(s => s.IsDynamic).OrderByDescending(s => s.Frequency == DrrPhysicalHz)
                    .Select(s => (RefreshRate?)s).FirstOrDefault();
                if (dynamic is { } drr)
                {
                    if (!(await feature.GetStateAsync().ConfigureAwait(false)).Equals(drr))
                        await feature.SetStateAsync(drr).ConfigureAwait(false);
                    return ParameterResult.Of(await VerifyPanelAsync(new PanelExpectation(drr.Frequency, drr.BaseFrequency, true)).ConfigureAwait(false));
                }
                var fixedResult = await ApplyPanelRateAsync(DrrPhysicalHz).ConfigureAwait(false);
                var fallback = Tr.F("DRR is unavailable (panel states: {0}), set to 120 Hz", string.Join(", ", states.Select(x => x.IsDynamic ? $"{x.Frequency} dyn/{x.BaseFrequency}" : x.Frequency.ToString())));
                return fixedResult is null ? ParameterResult.Note(fallback) : ParameterResult.Of(fallback + "; " + fixedResult);
            default:
                Log.Trace("panel rate skipped: do not touch");
                return ParameterResult.Ok;
        }
    }

    // The mismatch text, or null. The path settles a moment after the CCD write: one more look after a pause.
    static async Task<string?> VerifyPanelAsync(PanelExpectation want)
    {
        string? last = null;
        foreach (var delayMs in new[] { 400, 1500 })
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            PanelReading? got;
            try { got = await ReadPanelAsync().ConfigureAwait(false); }
            catch (Exception e) { Log.Error("panel read-back", e); return Tr.F("panel not read: {0}", e.Message); }
            if (got is not { } g) return null;   // no internal display: nothing to compare
            last = Compare(want, g);
            if (last is null) return null;
        }
        return last;
    }

    // null = equal (rates within 1 Hz, the flag exact).
    internal static string? Compare(PanelExpectation want, PanelReading got)
    {
        var problems = new List<string>();
        if (Math.Abs(got.PhysicalHz - want.PhysicalHz) > 1) problems.Add(Tr.F("signal {0} Hz, expected {1} Hz", got.PhysicalHz, want.PhysicalHz));
        if (got.Drr != want.Drr) problems.Add(Tr.F("DRR {0}, expected {1}", got.Drr ? Tr.T("on|2") : Tr.T("off"), want.Drr ? Tr.T("on|2") : Tr.T("off")));
        if (Math.Abs(got.VirtualHz - want.VirtualHz) > 1) problems.Add(Tr.F("frequency in the system {0} Hz, expected {1} Hz", got.VirtualHz, want.VirtualHz));
        return problems.Count == 0 ? null : Tr.F("panel: {0}", string.Join("; ", problems));
    }

    // The same reading for the Status tab.
    internal static async Task<Status.PanelFact?> ReadPanelFactAsync() =>
        await ReadPanelAsync().ConfigureAwait(false) is { } r ? new Status.PanelFact(r.PhysicalHz, r.VirtualHz, r.Drr) : null;

    // Signal frequency (VerticalSyncFrequencyInMillihertz), the boost (DRR) flag and the virtual rate of the internal target.
    static async Task<PanelReading?> ReadPanelAsync()
    {
        var display = await InternalDisplay.GetAsync().ConfigureAwait(false);
        if (display?.DisplayScreen is not { } screen) return null;
        var displaySource = screen.ToPathDisplaySource();
        var displayTarget = display.ToPathDisplayTarget();
        var path = PathInfo.GetActivePaths(true)
            .FirstOrDefault(p => p.DisplaySource == displaySource && (displayTarget is null || p.TargetsInfo.Any(t => t.DisplayTarget == displayTarget)));
        var target = path?.TargetsInfo.FirstOrDefault(t => displayTarget is null || t.DisplayTarget == displayTarget);
        if (target is null) return null;
        var physical = target.SignalInfo is null ? 0 : (int)(target.SignalInfo.VerticalSyncFrequencyInMillihertz / 1000);
        return new PanelReading(physical, screen.CurrentSetting.Frequency, target.IsBoostRefreshRate);
    }

    // Projection, then the rate of each external display. Skipped (null) while no external display is connected.
    public static async Task<string?> ApplyExternalAsync(ExternalMonitor p)
    {
        // Presence comes from all display paths, so a monitor that InternalOnly detached still counts.
        if (!await Task.Run(DisplayPresence.ExternalConnected).ConfigureAwait(false))
        {
            Log.Trace("external display parameters skipped: no external display");
            return null;
        }

        var errors = new List<string>();
        if (p.Projection != Projection.DoNotTouch)
        {
            try { SetProjection(p.Projection); }
            catch (Exception e) { Log.Error("projection", e); errors.Add("projection: " + e.Message); }
        }
        else Log.Trace("projection skipped: do not touch");

        if (p.Rate.Kind == ExternalRateKind.DoNotTouch) Log.Trace("external rate skipped: do not touch");
        else
        {
            try
            {
                // Only the attached displays can have a rate set; the projection change can change that set.
                var displays = await ExternalDisplays.GetAsync().ConfigureAwait(false);
                if (displays.Length == 0) Log.Trace("external rate skipped: no attached external display");
                foreach (var display in displays)
                {
                    try { ApplyRate(display, p.Rate); }
                    catch (Exception e) { Log.Error("external display rate", e); errors.Add("external rate: " + e.Message); }
                }
            }
            catch (Exception e) { Log.Error("external displays", e); errors.Add("external displays: " + e.Message); }
        }
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    static void SetProjection(Projection projection)
    {
        var topology = projection switch
        {
            Projection.Extend => SdcTopologyExtend,
            Projection.Duplicate => SdcTopologyClone,
            Projection.ExternalOnly => SdcTopologyExternal,
            Projection.InternalOnly => SdcTopologyInternal,
            _ => 0u,
        };
        if (topology == 0) return;
        var rc = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SdcApply | topology);
        if (rc != 0) throw new InvalidOperationException($"SetDisplayConfig failed: {rc}");
    }

    static void ApplyRate(Display display, ExternalRate rate)
    {
        var screen = display.DisplayScreen;   // null for a detached display
        if (screen is null) return;
        var current = screen.CurrentSetting;
        var candidates = screen.GetPossibleSettings()
            .Where(s => Math.Max(s.Resolution.Width, s.Resolution.Height) >= 1000
                        && s.Resolution == current.Resolution && s.ColorDepth == current.ColorDepth && s.IsInterlaced == current.IsInterlaced)
            .ToArray();
        int? hz = rate.Kind switch
        {
            ExternalRateKind.Maximum => candidates.Length == 0 ? null : candidates.Max(s => s.Frequency),
            ExternalRateKind.Fixed => Pick(candidates.Select(s => s.Frequency), rate.Hz),
            _ => null,
        };
        if (hz is not { } target || target == current.Frequency) return;
        var chosen = candidates.First(s => s.Frequency == target);
        var setting = new DisplaySetting(chosen, current.Position, current.Orientation, DisplayFixedOutput.Default);
        screen.SetSettings(setting, true);
    }
}
