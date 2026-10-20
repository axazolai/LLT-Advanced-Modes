using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Core.Programs;

namespace LegionModes.Plugin.Ui.ViewModels;

// How a value is coloured: Good = PaletteGreenBrush, Bad = critical red, Warn = PaletteOrangeBrush, Muted = Tertiary.
public enum Tone { Normal, Good, Bad, Warn, Muted }

// "Label: value" row of a card.
public sealed record StatusLine(string Label, string Value, Tone Tone = Tone.Normal);

// One effective parameter: its value, where it comes from and its last apply result (Result is empty for a success, with
// or without a note).
public sealed record ParameterLine(ParameterId Id, string Name, string Value, string Source, string Result, Tone Tone, string? Detail);

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

    string _inForceName = "—", _inForceReason = "", _applyLine = "", _microphone = "", _camera = "", _wakeState = Tr.T("читается…");
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
        var source = a.Source switch { PowerSource.Ac => Tr.T("сеть"), PowerSource.Weak => Tr.T("слабый блок"), _ => Tr.T("батарея") };
        var lid = a.Lid == LidState.Open ? Tr.T("открыт") : Tr.T("закрыт");
        var load = a.Load switch { Workload.Heavy => Tr.T("тяжёлая нагрузка"), Workload.Ai => Tr.T("ИИ"), Workload.Work => Tr.T("работа"), _ => Tr.T("простой") };
        var text = $"{source}, {lid}, {load}";
        return a.Monitor == MonitorState.Connected ? text + Tr.T(", внешний монитор") : text;
    }

    public static string OverrideName(OverrideKind k, Workload? w) => k switch
    {
        OverrideKind.ForceWorkload => Tr.T("Нагрузка: ") + w switch
        {
            Workload.Heavy => Tr.T("тяжёлая"), Workload.Ai => Tr.T("ИИ"), Workload.Work => Tr.T("работа"), _ => Tr.T("простой"),
        },
        OverrideKind.Autonomy => Tr.T("Автономия"),
        OverrideKind.StayAwake => Tr.T("Не спать"),
        OverrideKind.RapidCharge => Tr.T("Быстрая зарядка"),
        OverrideKind.ChargeFull => Tr.T("Зарядить до 100 %"),
        OverrideKind.DGpu => Tr.T("Дискретная видеокарта"),
        _ => Tr.T("Пауза"),
    };

    public static string NoteText(RuleNote n) => n switch
    {
        RuleNote.ExternalMonitorKeepsHybrid => Tr.T("Внешний монитор подключён к дискретной карте: «Только встроенная» применяется как «Гибридный»"),
        _ => Tr.T("Крышка закрыта: «Только встроенный» экран не применяется"),
    };

    // "18 · Батарея, открыт, работа"
    public static string ProfileTitle(ModesConfig c, Profile p)
    {
        var index = c.IndexOf(p);
        return (index >= 0 ? $"{index} · " : "") + ProfileNames.Display(p);
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
            InForceReason = Tr.T("профиль ещё не выбран");
            InForceTone = Tone.Muted;
        }
        else
        {
            InForceName = ProfileTitle(config, d.Profile);
            InForceReason = d.Paused ? d.Reason + Tr.T(" · пауза, параметры не применяются")
                : !_runtime.HasControl ? d.Reason + Tr.T(" · управление у LLT, параметры не применяются")
                : d.Reason;
            InForceTone = d.Paused || !_runtime.HasControl ? Tone.Warn : Tone.Good;
        }

        var rows = new List<StatusLine>();
        if (d is not null) rows.Add(new(Tr.T("Условия"), AxesName(d.Axes)));
        rows.Add(new(Tr.T("Управление"), status.HasControl ? Tr.T("плагин управляет режимами") : Tr.T("у LLT"), status.HasControl ? Tone.Normal : Tone.Warn));
        if (d is not null)
        {
            foreach (var p in d.Pending) rows.Add(new(Tr.T("Ждёт"), Tr.F("«{0}» вступит в силу в {1:HH:mm:ss}", ProfileNames.Display(p.Profile), p.At.ToLocalTime())));
            foreach (var o in d.Overrides) rows.Add(new(Tr.T("Включено"), OverrideName(o.Kind, o.Workload) + Left(o.Until), Tone.Warn));
            foreach (var n in d.Notes) rows.Add(new(Tr.T("Правило"), NoteText(n)));
            if (d.ForcedSleepAt is { } sleep) rows.Add(new(Tr.T("Сон с закрытой крышкой"), Tr.F("в {0:HH:mm:ss}", sleep.ToLocalTime())));
        }
        ProfileRows = rows;

        Parameters = d is null ? [] : Enum.GetValues<ParameterId>().Select(id => ParameterOf(id, d, config, status)).ToArray();

        ApplyLine = status.LastApplyAt is null ? Tr.T("ещё не применялось") : $"{status.LastApplyAt.Value.ToLocalTime():HH:mm:ss}";
        var plugin = new List<StatusLine> { new(Tr.T("Последнее применение"), ApplyLine) };
        if (status.LastApplyError is { } err) plugin.Add(new(Tr.T("Сообщение"), err, Tone.Bad));
        plugin.Add(status.SelfTestOk switch
        {
            true => new StatusLine(Tr.T("Самопроверка"), Tr.T("пройдена"), Tone.Good),
            false => new StatusLine(Tr.T("Самопроверка"), Tr.T("не пройдена: ") + string.Join("; ", status.SelfTestFailures), Tone.Bad),
            _ => new StatusLine(Tr.T("Самопроверка"), Tr.T("ещё не выполнялась"), Tone.Muted),
        });
        if (status.PipelineError is { } pl) plugin.Add(new(Tr.T("Действия LLT"), pl, Tone.Bad));
        if (status.HardwareNote is { } hw) plugin.Add(new(Tr.T("Оборудование"), hw, Tone.Warn));
        if (status.ShellError is { } sh) plugin.Add(new(Tr.T("Трей и главная"), sh, Tone.Bad));
        if (status.TranslationProblems is { } tr) plugin.Add(new(Tr.T("Тексты LLT"), Tr.T("не исправлены: ") + tr, Tone.Warn));
        PluginRows = plugin;

        var errors = new List<string>();
        if (status.ConfigError is { } ce)
            errors.Add(Tr.F("Ошибка modes.json: {0}. Показаны заводские значения; файл не изменён, сохранение на любой вкладке запишет полный рабочий файл.", ce));
        if (status.ProgramsError is { } pe) errors.Add(Tr.F("Ошибка programs.json: {0}", pe));
        if (status.ControlError is { } cte)
            errors.Add(Tr.F("Не удалось прочитать control.json: {0}. Запись в него отключена, копия сохранена в control.unreadable.json; " +
                            "запись включится, когда файл прочитается или когда вы возьмёте или вернёте управление на вкладке «Управление».", cte));
        Errors = errors;

        var dev = _runtime.Devices;
        Microphone = dev.MicrophoneText;
        Camera = dev.CameraText;
        var devices = new List<StatusLine> { new(Tr.T("Микрофон"), dev.MicrophoneWord, dev.MicMuted is null ? Tone.Muted : Tone.Normal) };
        if (dev.MicInUseBy.Length > 0) devices.Add(new(Tr.T("Используют"), Status.DeviceStatus.UsersList(dev.MicInUseBy)));
        devices.Add(new(Tr.T("Камера"), dev.CameraPresence, dev.Camera == Status.CameraState.Present ? Tone.Normal : Tone.Muted));
        devices.Add(new(Tr.T("Переключатель"), dev.SwitchText, dev.Switch is null ? Tone.Muted : Tone.Normal));
        if (dev.CameraInUseBy.Length > 0) devices.Add(new(Tr.T("Используют"), Status.DeviceStatus.UsersList(dev.CameraInUseBy)));
        DeviceRows = devices;
    }

    static string Left(DateTimeOffset? until)
    {
        if (until is not { } u) return "";
        var left = u - DateTimeOffset.Now;
        return left > TimeSpan.Zero ? Tr.F(" · осталось {0}:{1:00}", (int)left.TotalMinutes, left.Seconds) : Tr.T(" · истекает");
    }

    // The value in force, its source profile (or what changed it) and the last apply result.
    static ParameterLine ParameterOf(ParameterId id, Decision d, ModesConfig config, StatusModel status)
    {
        var value = id == ParameterId.Power ? d.Parameters.Power.Value : ParameterDiff.ValueOf(d.Parameters, id);
        var source = SourceOf(d.Parameters, id);
        var sourceText = source is null ? "" : source.Id == d.Profile.Id ? Tr.T("задан профилем") : Tr.F("от «{0}»", ProfileNames.Display(source));
        if (!Equals(ParameterDiff.ValueOf(d.Parameters, id), ParameterDiff.ValueOf(Resolver.Effective(config, d.Profile), id)))
            sourceText = ChangedBy(id, d);
        var (result, tone, detail) = status.ParameterResults.TryGetValue(id, out var res)
            ? res == "ok" ? ("", Tone.Good, (string?)null)
            : res == "skipped" ? (Tr.T("пропущен"), Tone.Muted, null)
            : res.StartsWith(StatusModel.NotePrefix, StringComparison.Ordinal) ? ("", Tone.Warn, res[StatusModel.NotePrefix.Length..])
            : (Tr.T("ошибка"), Tone.Bad, res)
            : (Tr.T("не применялся"), Tone.Muted, null);
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
            ParameterId.Timers when Has(OverrideKind.StayAwake) => Tr.F("изменено: «{0}»", Tr.T("Не спать")),
            ParameterId.Charging => Tr.F("изменено: «{0}»", d.Overrides.LastOrDefault(o => o.Kind is OverrideKind.RapidCharge or OverrideKind.ChargeFull) is { } o
                ? OverrideName(o.Kind, null) : Tr.T("быстрое действие")),
            ParameterId.Gpu => Tr.T("изменено правилом внешнего монитора"),
            ParameterId.External => Tr.T("изменено правилом закрытой крышки"),
            _ => Tr.T("изменено"),
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
            WakeState = Wake.Count == 0 ? Tr.T("никто: ноутбук может гасить экран и засыпать") : "";
        }
        catch (Exception e)
        {
            Log.Error("wake requests", e);
            WakeState = Tr.F("не удалось прочитать: {0}", e.Message);
        }
        finally { Volatile.Write(ref _readingWake, 0); }
    }

    static string KindText(string kind) => kind.ToUpperInvariant() switch
    {
        "DISPLAY" => Tr.T("держит экран"),
        "SYSTEM" => Tr.T("не даёт уснуть"),
        "AWAYMODE" => Tr.T("режим отсутствия"),
        "EXECUTION" => Tr.T("выполнение"),
        "PERFBOOST" => Tr.T("ускорение"),
        "ACTIVELOCKSCREEN" => Tr.T("экран блокировки"),
        _ => kind.ToLowerInvariant(),
    };

    static string CallerText(string caller) => caller.ToUpperInvariant() switch
    {
        "PROCESS" => Tr.T("программа"),
        "SERVICE" => Tr.T("служба"),
        "DRIVER" => Tr.T("драйвер"),
        _ => caller.ToLowerInvariant(),
    };
}
