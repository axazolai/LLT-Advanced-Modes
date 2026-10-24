using System.Management;
using LegionModes.Core;
using LegionModes.Core.Model;
using LegionModes.Core.Status;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Utils;

namespace LegionModes.Plugin.Status;

public readonly record struct PanelFact(int PhysicalHz, int VirtualHz, bool Drr);

// The external displays now: whether any is attached, the projection the display paths show (null when it cannot be told), each one's rate.
public sealed record ExternalFact(bool Attached, Projection? Projection, IReadOnlyList<int> Rates);

public sealed record TimersFact(LegionModes.Core.Engine.WindowsTimerValues Values, bool OnBattery);

public sealed record LightingFact(int Profile, int Brightness);

public sealed record ProcessorFact(uint? Boost, uint? Epp, LegionModes.Core.Model.WindowsPowerMode? Overlay, bool OnBattery);

// kind null = the driver did not answer; percent and flow come from the battery's own WMI classes.
public sealed record ChargeFact(ChargeKind? Kind, int? Percent, string Flow);

// What the panel and the charger are doing now, read from the display path (QueryDisplayConfig) and the battery (the Lenovo charging
// driver, Windows' battery WMI) every 2 s while the Status tab is shown; a hidden tab reads nothing. Every read is a live read, never a
// cached value (decision 35 of spec 16).
public sealed class LiveFacts : IDisposable
{
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    readonly object _gate = new();
    System.Threading.Timer? _timer;
    PanelFact? _panel;
    ChargeFact? _charge;
    ExternalFact? _external;
    UsbKind? _usb;
    ProcessorFact? _processor;
    TimersFact? _timers;
    LightingFact? _lighting;
    int _rounds;
    volatile bool _watching;
    int _reading;
    int _disposed;

    public void Start() => _timer = new System.Threading.Timer(_ => _ = ReadAsync(), null, Timeout.Infinite, Timeout.Infinite);

    public void Watch(bool on)
    {
        _watching = on;
        _timer?.Change(on ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, on ? Poll : Timeout.InfiniteTimeSpan);
    }

    public PanelFact? Panel { get { lock (_gate) return _panel; } }
    public ChargeFact? Charge { get { lock (_gate) return _charge; } }
    public ExternalFact? External { get { lock (_gate) return _external; } }
    public UsbKind? Usb { get { lock (_gate) return _usb; } }
    public ProcessorFact? Processor { get { lock (_gate) return _processor; } }
    public TimersFact? Timers { get { lock (_gate) return _timers; } }
    public LightingFact? Lighting { get { lock (_gate) return _lighting; } }

    async Task ReadAsync()
    {
        if (!_watching || Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _reading, 1) == 1) return;
        try
        {
            var panel = await ReadPanelAsync().ConfigureAwait(false);
            var charge = await ReadChargeAsync().ConfigureAwait(false);
            var external = await ReadExternalAsync().ConfigureAwait(false);
            var usb = await ReadUsbAsync().ConfigureAwait(false);
            var processor = ReadProcessor();
            var timers = ReadTimers();
            // The keyboard is asked every third round: its request and answer share one report, and LLT's own window uses it too.
            var lighting = _rounds++ % 3 == 0 ? await ReadLightingAsync().ConfigureAwait(false) : null;
            lock (_gate)
            {
                _panel = panel; _charge = charge; _external = external; _usb = usb; _processor = processor; _timers = timers;
                if (lighting is not null) _lighting = lighting;
            }
        }
        catch (Exception e) { Log.Error("live facts read", e); }
        finally { Volatile.Write(ref _reading, 0); }
    }

    static async Task<PanelFact?> ReadPanelAsync()
    {
        try { return await Apply.Displays.ReadPanelFactAsync().ConfigureAwait(false); }
        catch (Exception e) { Log.Error("live facts: panel", e); return null; }
    }

    // Attached displays from the system's own display enumeration. Two attached displays at the same position are a duplicate, at different
    // positions an extension; one attached display is internal only or external only.
    static async Task<ExternalFact?> ReadExternalAsync()
    {
        try
        {
            if (!await Task.Run(Apply.DisplayPresence.ExternalConnected).ConfigureAwait(false)) return new ExternalFact(false, null, []);
            var externals = await ExternalDisplays.GetAsync().ConfigureAwait(false);
            var attached = externals.Where(d => d.DisplayScreen is not null).ToArray();
            var internalScreen = (await InternalDisplay.GetAsync().ConfigureAwait(false))?.DisplayScreen;
            Projection? projection = null;
            if (attached.Length == 0 && internalScreen is not null) projection = Projection.InternalOnly;
            else if (attached.Length > 0 && internalScreen is null) projection = Projection.ExternalOnly;
            else if (attached.Length > 0 && internalScreen is not null)
                projection = attached.Any(d => d.DisplayScreen!.CurrentSetting.Position == internalScreen.CurrentSetting.Position) ? Projection.Duplicate : Projection.Extend;
            return new ExternalFact(true, projection, attached.Select(d => d.DisplayScreen!.CurrentSetting.Frequency).ToArray());
        }
        catch (Exception e) { Log.Error("live facts: external displays", e); return null; }
    }

    static async Task<UsbKind?> ReadUsbAsync()
    {
        try
        {
            var feature = IoCContainer.Resolve<AlwaysOnUSBFeature>();
            if (!await feature.IsSupportedAsync().ConfigureAwait(false)) return null;
            return await feature.GetStateAsync().ConfigureAwait(false) switch
            {
                AlwaysOnUSBState.OnAlways => UsbKind.Always,
                AlwaysOnUSBState.OnWhenSleeping => UsbKind.WhenSleeping,
                _ => UsbKind.Off,
            };
        }
        catch (Exception e) { Log.Error("live facts: always-on USB", e); return null; }
    }

    static TimersFact? ReadTimers()
    {
        try
        {
            var (values, onBattery) = Apply.WindowsPower.ReadTimers();
            return new TimersFact(values, onBattery);
        }
        catch (Exception e) { Log.Error("live facts: timers", e); return null; }
    }

    // The keyboard's own answer about its profile slot and brightness (a live request to the device).
    static async Task<LightingFact?> ReadLightingAsync()
    {
        try
        {
            var controller = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();
            if (!await controller.IsSupportedAsync().ConfigureAwait(false)) return null;
            var profile = await controller.GetProfileAsync().ConfigureAwait(false);
            var brightness = await controller.GetBrightnessAsync().ConfigureAwait(false);
            return new LightingFact(profile, brightness);
        }
        catch (Exception e) { Log.Error("live facts: lighting", e); return null; }
    }

    static ProcessorFact? ReadProcessor()
    {
        try
        {
            var (boost, epp, overlay, onBattery) = Apply.WindowsPower.ReadProcessor();
            return new ProcessorFact(boost, epp, overlay, onBattery);
        }
        catch (Exception e) { Log.Error("live facts: processor", e); return null; }
    }

    static async Task<ChargeFact?> ReadChargeAsync()
    {
        ChargeKind? kind = null;
        try
        {
            var feature = IoCContainer.Resolve<BatteryFeature>();
            if (await feature.IsSupportedAsync().ConfigureAwait(false))
                kind = await feature.GetStateAsync().ConfigureAwait(false) switch
                {
                    BatteryState.Conservation => ChargeKind.Conservation,
                    BatteryState.RapidCharge => ChargeKind.Rapid,
                    _ => ChargeKind.Normal,
                };
        }
        catch (Exception e) { Log.Error("live facts: charging mode", e); }
        return await Task.Run(() => ReadBattery(kind)).ConfigureAwait(false);
    }

    static ChargeFact? ReadBattery(ChargeKind? kind)
    {
        try
        {
            int? percent = null;
            using (var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT EstimatedChargeRemaining FROM Win32_Battery"))
            using (var all = s.Get())
                foreach (ManagementObject o in all) using (o) { percent = Convert.ToInt32(o["EstimatedChargeRemaining"]); break; }

            var flow = "";
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT Charging, Discharging, PowerOnline, ChargeRate, DischargeRate FROM BatteryStatus"))
            using (var all = s.Get())
                foreach (ManagementObject o in all)
                    using (o)
                    {
                        var charging = Convert.ToBoolean(o["Charging"]);
                        var discharging = Convert.ToBoolean(o["Discharging"]);
                        var online = Convert.ToBoolean(o["PowerOnline"]);
                        var chargeW = Convert.ToInt32(o["ChargeRate"]) / 1000.0;
                        var dischargeW = Convert.ToInt32(o["DischargeRate"]) / 1000.0;
                        flow = charging ? Tr.F("charging {0:0.#} W", chargeW)
                            : discharging ? Tr.F("on battery, drawing {0:0.#} W", dischargeW)
                            : online ? Tr.T("on mains, not charging") : "";
                        break;
                    }
            return new ChargeFact(kind, percent, flow);
        }
        catch (Exception e)
        {
            Log.Error("live facts: battery", e);
            return kind is null ? null : new ChargeFact(kind, null, "");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _watching = false;
        _timer?.Dispose();
    }
}
