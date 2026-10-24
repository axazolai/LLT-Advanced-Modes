using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Core.Programs;
using LegionModes.Core.Status;

namespace LegionModes.Plugin.Ui.ViewModels;

// How a value is coloured: Good = PaletteGreenBrush, Bad = critical red, Warn = PaletteOrangeBrush, Muted = Tertiary.
public enum Tone { Normal, Good, Bad, Warn, Muted }

// "Label: value" row of a card.
public sealed record StatusLine(string Label, string Value, Tone Tone = Tone.Normal);

// One effective parameter: its value, where it comes from and its last apply result (Result is empty for a success, with
// or without a note).
public sealed record ParameterLine(ParameterId Id, string Name, string Value, string Source, string Result, Tone Tone, string? Detail, IReadOnlyList<StatusLine>? Facts = null);

// One holder of a wake request, all its request kinds joined.
public sealed record WakeLine(string Name, string What);

// What the Status tab, the dashboard card and the tray show. Read from the runtime only; no logic of its own.
public sealed class StatusViewModel : ViewModelBase
{
    readonly IModesHost _runtime;
    int _readingWake;

    public StatusViewModel(IModesHost runtime)
    {
        _runtime = runtime;
        Refresh();
    }

    string _inForceName = "—", _inForceReason = "", _applyLine = "", _microphone = "", _camera = "", _wakeState = Tr.T("reading…");
    Tone _inForceTone = Tone.Muted;
    IReadOnlyList<StatusLine> _profileRows = [], _pluginRows = [], _deviceRows = [];
    IReadOnlyList<ParameterLine> _parameters = [];
    IReadOnlyList<string> _errors = [];
    IReadOnlyList<WakeLine> _wake = [];

    public string InForceName { get => _inForceName; private set => Set(ref _inForceName, value); }
    public string InForceReason { get => _inForceReason; private set => Set(ref _inForceReason, value); }
    public Tone InForceTone { get => _inForceTone; private set => Set(ref _inForceTone, value); }
    public string ApplyLine { get => _applyLine; private set => Set(ref _applyLine, value); }
    public string Microphone { get => _microphone; private set => Set(ref _microphone, value); }
    public string Camera { get => _camera; private set => Set(ref _camera, value); }
    public IReadOnlyList<StatusLine> ProfileRows { get => _profileRows; private set => SetList(ref _profileRows, value); }
    public IReadOnlyList<StatusLine> PluginRows { get => _pluginRows; private set => SetList(ref _pluginRows, value); }
    public IReadOnlyList<StatusLine> DeviceRows { get => _deviceRows; private set => SetList(ref _deviceRows, value); }
    public IReadOnlyList<ParameterLine> Parameters { get => _parameters; private set => SetList(ref _parameters, value); }
    public IReadOnlyList<string> Errors { get => _errors; private set => SetList(ref _errors, value); }
    public IReadOnlyList<WakeLine> Wake { get => _wake; private set => SetList(ref _wake, value); }
    public string WakeState { get => _wakeState; private set => Set(ref _wakeState, value); }
    public Decision? Decision => _runtime.Last;
    public ModesConfig Config => _runtime.Config;

    void SetList<T>(ref IReadOnlyList<T> field, IReadOnlyList<T> value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field.SequenceEqual(value)) return;
        field = value;
        Raise(name);
    }

    // "сеть, открыт, тяжёлая нагрузка, внешний монитор"
    public static string AxesName(AxisState a)
    {
        var source = a.Source switch { PowerSource.Ac => Tr.T("AC"), PowerSource.Weak => Tr.T("weak adapter"), _ => Tr.T("battery") };
        var lid = a.Lid == LidState.Open ? Tr.T("open") : Tr.T("closed");
        var load = a.Load switch { Workload.Heavy => Tr.T("heavy load"), Workload.Ai => Tr.T("AI"), Workload.Work => Tr.T("work"), _ => Tr.T("idle") };
        var text = $"{source}, {lid}, {load}";
        return a.Monitor == MonitorState.Connected ? text + Tr.T(", external monitor") : text;
    }

    public static string OverrideName(OverrideKind k, Workload? w) => k switch
    {
        OverrideKind.ForceWorkload => Tr.T("Load: ") + w switch
        {
            Workload.Heavy => Tr.T("heavy"), Workload.Ai => Tr.T("AI"), Workload.Work => Tr.T("work"), _ => Tr.T("idle"),
        },
        OverrideKind.Autonomy => Tr.T("Autonomy"),
        OverrideKind.StayAwake => Tr.T("Stay awake"),
        OverrideKind.RapidCharge => Tr.T("Rapid charge"),
        OverrideKind.ChargeFull => Tr.T("Charge to 100 %"),
        OverrideKind.DGpu => Tr.T("Discrete GPU"),
        _ => Tr.T("Pause"),
    };

    public static string NoteText(RuleNote n) => n switch
    {
        RuleNote.ExternalMonitorKeepsHybrid => Tr.T("The external monitor is attached to the discrete GPU: \"Integrated only\" is applied as \"Hybrid\""),
        _ => Tr.T("Lid closed: \"Internal only\" display is not applied"),
    };

    // "18 · Батарея, открыт, работа"
    public static string ProfileTitle(ModesConfig c, Profile p)
    {
        var index = c.IndexOf(p);
        return (index >= 0 ? $"{index} · " : "") + ProfileNames.Display(p);
    }

    // The tab is on screen or not: the discrete card's state is polled only while it is shown.
    public void Shown(bool on)
    {
        _runtime.GpuLive?.Watch(on);
        _runtime.Facts?.Watch(on);
    }

    // Called by the page's one-second timer (the time left changes) and on every status or decision change.
    public void Refresh()
    {
        var status = _runtime.Status;
        var d = _runtime.Last;
        var config = _runtime.Config;

        if (d is null)
        {
            InForceName = "—";
            InForceReason = Tr.T("no profile selected yet");
            InForceTone = Tone.Muted;
        }
        else
        {
            InForceName = ProfileTitle(config, d.Profile);
            InForceReason = d.Paused ? d.Reason + Tr.T(" · paused, parameters are not applied")
                : !_runtime.HasControl ? d.Reason + Tr.T(" · LLT has control, parameters are not applied")
                : d.Reason;
            InForceTone = d.Paused || !_runtime.HasControl ? Tone.Warn : Tone.Good;
        }

        var rows = new List<StatusLine>();
        if (d is not null) rows.Add(new(Tr.T("Conditions"), AxesName(d.Axes)));
        rows.Add(new(Tr.T("Control"), status.HasControl ? Tr.T("the plugin controls the modes") : Tr.T("LLT"), status.HasControl ? Tone.Normal : Tone.Warn));
        if (d is not null)
        {
            foreach (var p in d.Pending) rows.Add(new(Tr.T("Waiting"), Tr.F("\"{0}\" takes effect at {1:HH:mm:ss}", ProfileNames.Display(p.Profile), p.At.ToLocalTime())));
            foreach (var o in d.Overrides) rows.Add(new(Tr.T("On"), OverrideName(o.Kind, o.Workload) + Left(o.Until), Tone.Warn));
            foreach (var n in d.Notes) rows.Add(new(Tr.T("Rule"), NoteText(n)));
            if (d.ForcedSleepAt is { } sleep) rows.Add(new(Tr.T("Sleep with lid closed"), Tr.F("at {0:HH:mm:ss}", sleep.ToLocalTime())));
        }
        ProfileRows = rows;

        Parameters = d is null ? [] : Enum.GetValues<ParameterId>().Select(id => ParameterOf(id, d, config, status) with { Facts = FactsOf(id, d) }).ToArray();

        ApplyLine = status.LastApplyAt is null ? Tr.T("not applied yet") : $"{status.LastApplyAt.Value.ToLocalTime():HH:mm:ss}";
        var plugin = new List<StatusLine> { new(Tr.T("Last apply"), ApplyLine) };
        if (status.LastApplyError is { } err) plugin.Add(new(Tr.T("Message"), err, Tone.Bad));
        plugin.Add(status.SelfTestOk switch
        {
            true => new StatusLine(Tr.T("Self-test"), Tr.T("passed"), Tone.Good),
            false => new StatusLine(Tr.T("Self-test"), Tr.T("failed: ") + string.Join("; ", status.SelfTestFailures), Tone.Bad),
            _ => new StatusLine(Tr.T("Self-test"), Tr.T("not run yet"), Tone.Muted),
        });
        if (status.PipelineError is { } pl) plugin.Add(new(Tr.T("LLT actions"), pl, Tone.Bad));
        if (status.HardwareNote is { } hw) plugin.Add(new(Tr.T("Hardware"), hw, Tone.Warn));
        if (status.ShellError is { } sh) plugin.Add(new(Tr.T("Tray and home"), sh, Tone.Bad));
        if (status.TranslationProblems is { } tr) plugin.Add(new(Tr.T("LLT texts"), Tr.T("not fixed: ") + tr, Tone.Warn));
        PluginRows = plugin;

        var errors = new List<string>();
        if (status.ConfigError is { } ce)
            errors.Add(Tr.F("modes.json error: {0}. Factory values are shown; the file is unchanged, and saving on any tab will write a complete working file.", ce));
        if (status.ProgramsError is { } pe) errors.Add(Tr.F("programs.json error: {0}", pe));
        if (status.ControlError is { } cte)
            errors.Add(Tr.F("Could not read control.json: {0}. Writing to it is disabled and a copy is kept in control.unreadable.json; writing resumes once the file can be read or when you take or return control on the \"Control\" tab.", cte));
        Errors = errors;

        var dev = _runtime.Devices;
        Microphone = dev.MicrophoneText;
        Camera = dev.CameraText;
        var devices = new List<StatusLine> { new(Tr.T("Microphone"), dev.MicrophoneWord, dev.MicMuted is null ? Tone.Muted : Tone.Normal) };
        if (dev.MicInUseBy.Length > 0) devices.Add(new(Tr.T("Used by"), Status.DeviceStatus.UsersList(dev.MicInUseBy)));
        devices.Add(new(Tr.T("Camera"), dev.CameraPresence, dev.Camera == Status.CameraState.Present ? Tone.Normal : Tone.Muted));
        devices.Add(new(Tr.T("Switch"), dev.SwitchText, dev.Switch is null ? Tone.Muted : Tone.Normal));
        if (dev.CameraInUseBy.Length > 0) devices.Add(new(Tr.T("Used by"), Status.DeviceStatus.UsersList(dev.CameraInUseBy)));
        DeviceRows = devices;
    }

    static string Left(DateTimeOffset? until)
    {
        if (until is not { } u) return "";
        var left = u - DateTimeOffset.Now;
        return left > TimeSpan.Zero ? Tr.F(" · {0}:{1:00} left", (int)left.TotalMinutes, left.Seconds) : Tr.T(" · expiring");
    }

    // What the hardware says, read by the plugin itself, set beside what the profile asked for. Only where the plugin has control a
    // difference is a warning; under LLT's control it is only shown.
    IReadOnlyList<StatusLine>? FactsOf(ParameterId id, Decision d)
    {
        var live = _runtime.GpuLive;
        if (live is null) return null;
        var judge = _runtime.HasControl && !d.Paused;
        switch (id)
        {
            case ParameterId.Gpu:
            {
                var lines = new List<StatusLine>();
                var actual = live.FirmwareGpu;
                if (actual is null) lines.Add(new(Tr.T("In fact"), Tr.T("no data"), Tone.Muted));
                else if (actual.DiscreteOnly) lines.Add(new(Tr.T("In fact"), Tr.T("Discrete only"), judge ? Tone.Warn : Tone.Normal));
                else
                {
                    var asked = d.Parameters.Gpu.Value;
                    var ok = FirmwareFacts.GpuMatches(actual, asked);
                    lines.Add(new(Tr.T("In fact"), ok || !judge ? ValueText.Gpu(actual.Mode!.Value) : Tr.F("{0}; the profile asks for \"{1}\"", ValueText.Gpu(actual.Mode!.Value), ValueText.Gpu(asked)),
                        !judge ? Tone.Normal : ok ? Tone.Good : Tone.Warn));
                }
                var (text, tone) = live.Current();
                lines.Add(new(Tr.T("Discrete card"), text, tone));
                return lines;
            }
            case ParameterId.Power:
            {
                var actual = live.FirmwarePower;
                if (actual is null) return [new(Tr.T("In fact"), Tr.T("no data"), Tone.Muted)];
                var ok = FirmwareFacts.PowerMatches(actual, d.Parameters.Power.Value);
                var word = PowerWord(actual);
                return [new(Tr.T("In fact"), ok || !judge ? word : Tr.F("{0}; the profile asks for \"{1}\"", word, AskedPower(d.Parameters.Power.Value)),
                    !judge ? Tone.Normal : ok ? Tone.Good : Tone.Warn)];
            }
            case ParameterId.Panel:
            {
                if (_runtime.Facts?.Panel is not { } p) return [new(Tr.T("In fact"), Tr.T("no data"), Tone.Muted)];
                var asked = d.Parameters.Panel.Value;
                var ok = HardwareFacts.PanelMatches(asked, p.PhysicalHz, p.Drr);
                var word = Tr.F("signal {0} Hz, system {1} Hz, DRR {2}", p.PhysicalHz, p.VirtualHz, p.Drr ? Tr.T("on|2") : Tr.T("off"));
                return [new(Tr.T("In fact"), ok || !judge ? word : Tr.F("{0}; the profile asks for \"{1}\"", word, ValueText.Panel(asked)),
                    !judge || asked.Kind == PanelRateKind.DoNotTouch ? Tone.Normal : ok ? Tone.Good : Tone.Warn)];
            }
            case ParameterId.Charging:
            {
                if (_runtime.Facts?.Charge is not { } c) return [new(Tr.T("In fact"), Tr.T("no data"), Tone.Muted)];
                var asked = d.Parameters.Charging.Value;
                var parts = new List<string>();
                if (c.Kind is { } k) parts.Add(ValueText.Charging(k switch { ChargeKind.Conservation => ChargingMode.Conservation, ChargeKind.Rapid => ChargingMode.Rapid, _ => ChargingMode.Normal }));
                if (c.Percent is { } pct) parts.Add(pct + " %");
                if (c.Flow.Length > 0) parts.Add(c.Flow);
                var word = parts.Count == 0 ? Tr.T("no data") : string.Join(" · ", parts);
                var ok = c.Kind is { } kind && HardwareFacts.ChargeMatches(asked, kind);
                return [new(Tr.T("In fact"), ok || !judge || c.Kind is null ? word : Tr.F("{0}; the profile asks for \"{1}\"", word, ValueText.Charging(asked)),
                    !judge || asked == ChargingMode.DoNotTouch || c.Kind is null ? Tone.Normal : ok ? Tone.Good : Tone.Warn)];
            }
            case ParameterId.External:
            {
                if (_runtime.Facts?.External is not { } x) return [new(Tr.T("In fact"), Tr.T("no data"), Tone.Muted)];
                if (!x.Attached) return [new(Tr.T("In fact"), Tr.T("no external display connected"), Tone.Muted)];
                var asked = d.Parameters.External.Value;
                var word = x.Projection is { } pr ? ValueText.Projection(pr) : Tr.T("no data");
                if (x.Rates.Count > 0) word += " · " + string.Join(", ", x.Rates.Select(r => Tr.F("{0} Hz", r)));
                var ok = x.Projection is { } p2 && HardwareFacts.ProjectionMatches(asked.Projection, p2);
                return [new(Tr.T("In fact"), ok || !judge || x.Projection is null ? word : Tr.F("{0}; the profile asks for \"{1}\"", word, ValueText.Projection(asked.Projection)),
                    !judge || asked.Projection == Projection.DoNotTouch || x.Projection is null ? Tone.Normal : ok ? Tone.Good : Tone.Warn)];
            }
            case ParameterId.Usb:
            {
                if (_runtime.Facts?.Usb is not { } u) return [new(Tr.T("In fact"), Tr.T("no data"), Tone.Muted)];
                var asked = d.Parameters.Usb.Value;
                var word = ValueText.Usb(u switch { UsbKind.Always => UsbPower.Always, UsbKind.WhenSleeping => UsbPower.WhenSleeping, _ => UsbPower.Off });
                var ok = HardwareFacts.UsbMatches(asked, u);
                return [new(Tr.T("In fact"), ok || !judge ? word : Tr.F("{0}; the profile asks for \"{1}\"", word, ValueText.Usb(asked)),
                    !judge ? Tone.Normal : ok ? Tone.Good : Tone.Warn)];
            }
            case ParameterId.Processor:
            {
                if (_runtime.Facts?.Processor is not { } c) return [new(Tr.T("In fact"), Tr.T("no data"), Tone.Muted)];
                var asked = d.Parameters.Processor.Value;
                var parts = new List<string>();
                if (c.Boost is { } b) parts.Add(Tr.F("boost index {0}", b));
                if (c.Epp is { } e) parts.Add(Tr.F("EPP {0}", e));
                if (c.Overlay is { } o) parts.Add(Tr.F("Windows power mode: {0}", ValueText.WindowsMode(o)));
                parts.Add(c.OnBattery ? Tr.T("battery values") : Tr.T("mains values"));
                var word = string.Join(" · ", parts);
                var ok = HardwareFacts.ProcessorMatches(asked, c.Boost, c.Epp, c.Overlay);
                return [new(Tr.T("In fact"), ok || !judge ? word : Tr.F("{0}; differs from the profile", word),
                    !judge || (asked.Boost == BoostMode.DoNotTouch && asked.Epp is null && asked.PowerMode == WindowsPowerMode.DoNotTouch) ? Tone.Normal : ok ? Tone.Good : Tone.Warn)];
            }
            case ParameterId.Timers:
            {
                if (_runtime.Facts?.Timers is not { } t) return [new(Tr.T("In fact"), Tr.T("no data"), Tone.Muted)];
                var asked = WindowsTimers.From(d.Parameters.Timers.Value);
                var word = ValueText.Timers(WindowsTimers.ToIdle(t.Values)) + " · " + (t.OnBattery ? Tr.T("battery values") : Tr.T("mains values"));
                var ok = HardwareFacts.TimersMatch(asked, t.Values);
                return [new(Tr.T("In fact"), ok || !judge ? word : Tr.F("{0}; differs from the profile", word),
                    !judge ? Tone.Normal : ok ? Tone.Good : Tone.Warn)];
            }
            case ParameterId.Lighting:
            {
                if (_runtime.Facts?.Lighting is not { } l) return [new(Tr.T("In fact"), Tr.T("no data"), Tone.Muted)];
                var asked = d.Parameters.Lighting.Value;
                var word = l.Brightness == 0 ? Tr.F("profile {0} · off", l.Profile) : Tr.F("profile {0} · brightness {1}", l.Profile, l.Brightness);
                var ok = HardwareFacts.LightingMatches(asked, l.Profile, l.Brightness);
                return [new(Tr.T("In fact"), ok || !judge ? word : Tr.F("{0}; the profile asks for \"{1}\"", word, ValueText.Of(ParameterId.Lighting, asked, "")),
                    !judge ? Tone.Normal : ok ? Tone.Good : Tone.Warn)];
            }
            default: return null;
        }
    }

    static string AskedPower(PowerSetting p) => p.Firmware is { } f ? ValueText.Firmware(f) : Tr.T("Custom (GodMode)");

    static string PowerWord(FirmwarePower p) => p.Kind switch
    {
        PowerKind.Quiet => Tr.T("Quiet"),
        PowerKind.Balance => Tr.T("Balance"),
        PowerKind.Performance => Tr.T("Performance"),
        PowerKind.Custom => Tr.T("Custom (GodMode)"),
        PowerKind.Extreme => Tr.T("Extreme"),
        _ => Tr.F("mode {0}", p.Code),
    };

    // The value in force, its source profile (or what changed it) and the last apply result.
    static ParameterLine ParameterOf(ParameterId id, Decision d, ModesConfig config, StatusModel status)
    {
        var value = id == ParameterId.Power ? d.Parameters.Power.Value : ParameterDiff.ValueOf(d.Parameters, id);
        var source = SourceOf(d.Parameters, id);
        var sourceText = source is null ? "" : source.Id == d.Profile.Id ? Tr.T("set by the profile") : Tr.F("from \"{0}\"", ProfileNames.Display(source));
        if (!Equals(ParameterDiff.ValueOf(d.Parameters, id), ParameterDiff.ValueOf(Resolver.Effective(config, d.Profile), id)))
            sourceText = ChangedBy(id, d);
        var (result, tone, detail) = status.ParameterResults.TryGetValue(id, out var res)
            ? res == "ok" ? ("", Tone.Good, (string?)null)
            : res == "skipped" ? (Tr.T("skipped"), Tone.Muted, null)
            : res.StartsWith(StatusModel.NotePrefix, StringComparison.Ordinal) ? ("", Tone.Warn, res[StatusModel.NotePrefix.Length..])
            : (Tr.T("error"), Tone.Bad, res)
            : (Tr.T("not applied"), Tone.Muted, null);
        return new ParameterLine(id, Texts.Parameter[id], ValueText.Of(id, value, source is null ? "" : ProfileNames.Display(source)), sourceText, result, tone, detail);
    }

    static Profile? SourceOf(EffectiveParameters e, ParameterId id) => id switch
    {
        ParameterId.Power => e.Power.Source, ParameterId.Gpu => e.Gpu.Source, ParameterId.Panel => e.Panel.Source,
        ParameterId.External => e.External.Source, ParameterId.Charging => e.Charging.Source, ParameterId.Usb => e.Usb.Source,
        ParameterId.Processor => e.Processor.Source, ParameterId.Timers => e.Timers.Source, ParameterId.Lighting => e.Lighting.Source,
        _ => null,
    };

    // An override or a built-in rule changed the profile's value.
    static string ChangedBy(ParameterId id, Decision d)
    {
        bool Has(OverrideKind k) => d.Overrides.Any(o => o.Kind == k);
        return id switch
        {
            ParameterId.Timers when Has(OverrideKind.StayAwake) => Tr.F("changed: \"{0}\"", Tr.T("Stay awake")),
            ParameterId.Charging => Tr.F("changed: \"{0}\"", d.Overrides.LastOrDefault(o => o.Kind is OverrideKind.RapidCharge or OverrideKind.ChargeFull) is { } o
                ? OverrideName(o.Kind, null) : Tr.T("quick action")),
            ParameterId.Gpu => Tr.T("changed by the external monitor rule"),
            ParameterId.External => Tr.T("changed by the closed lid rule"),
            _ => Tr.T("changed"),
        };
    }

    // `powercfg /requests`: on open and every 10 s while the tab is shown.
    public async Task RefreshWakeAsync()
    {
        if (Interlocked.Exchange(ref _readingWake, 1) == 1) return;
        try
        {
            var requests = await _runtime.ReadWakeRequestsAsync();
            Wake = requests
                .GroupBy(r => (r.Name, r.Caller))
                .Select(g => new WakeLine(g.Key.Name,
                    string.Join(", ", g.Select(r => KindText(r.Kind)).Distinct()) + " · " + CallerText(g.Key.Caller)
                    + (g.Select(r => r.Detail).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) is { } detail ? " · " + detail : "")))
                .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            WakeState = Wake.Count == 0 ? Tr.T("none: the laptop may turn off the screen and sleep") : "";
        }
        catch (Exception e)
        {
            Log.Error("wake requests", e);
            WakeState = Tr.F("could not read: {0}", e.Message);
        }
        finally { Volatile.Write(ref _readingWake, 0); }
    }

    static string KindText(string kind) => kind.ToUpperInvariant() switch
    {
        "DISPLAY" => Tr.T("keeps the screen on"),
        "SYSTEM" => Tr.T("prevents sleep"),
        "AWAYMODE" => Tr.T("away mode"),
        "EXECUTION" => Tr.T("execution"),
        "PERFBOOST" => Tr.T("boost"),
        "ACTIVELOCKSCREEN" => Tr.T("lock screen"),
        _ => kind.ToLowerInvariant(),
    };

    static string CallerText(string caller) => caller.ToUpperInvariant() switch
    {
        "PROCESS" => Tr.T("program"),
        "SERVICE" => Tr.T("service"),
        "DRIVER" => Tr.T("driver"),
        _ => caller.ToLowerInvariant(),
    };
}
