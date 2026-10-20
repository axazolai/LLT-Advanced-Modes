using LegionModes.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;

namespace LegionModes.Plugin;

// Read by the UI. Values change on any thread; PropertyChanged is posted through `post` (the UI dispatcher).
public sealed class StatusModel : INotifyPropertyChanged
{
    readonly Action<Action> _post;

    // A parameter result that is a note (applied, with a stated difference) starts with this; it is not an error.
    public static string NotePrefix => Tr.T("заметка: ");

    public StatusModel(Action<Action>? post = null) => _post = post ?? (a => a());

    public event PropertyChangedEventHandler? PropertyChanged;

    PowerSource _source;
    LidState _lid;
    Workload _load;
    bool _externalDisplay;
    Decision? _decision;
    bool _hasControl;
    string? _configError;
    string? _programsError;
    string? _lastApplyError;
    string? _controlError;
    string? _hardwareNote;
    string? _pipelineError;
    string? _shellError;
    string? _translationProblems;
    DateTimeOffset? _lastApplyAt;
    bool? _selfTestOk;
    IReadOnlyList<string> _selfTestFailures = [];
    IReadOnlyDictionary<ParameterId, string> _parameterResults = new Dictionary<ParameterId, string>();

    public PowerSource Source { get => _source; internal set => Set(ref _source, value); }
    public LidState Lid { get => _lid; internal set => Set(ref _lid, value); }
    public Workload Load { get => _load; internal set => Set(ref _load, value); }
    public bool ExternalDisplay { get => _externalDisplay; internal set => Set(ref _externalDisplay, value); }
    public Decision? Decision { get => _decision; internal set => Set(ref _decision, value); }
    public bool HasControl { get => _hasControl; internal set => Set(ref _hasControl, value); }
    public string? ConfigError { get => _configError; internal set => Set(ref _configError, value); }
    public string? ProgramsError { get => _programsError; internal set => Set(ref _programsError, value); }
    // control.json could not be read at start: shown until a read succeeds or the owner takes or returns control.
    public string? ControlError { get => _controlError; internal set => Set(ref _controlError, value); }
    public string? LastApplyError { get => _lastApplyError; internal set => Set(ref _lastApplyError, value); }
    public string? HardwareNote { get => _hardwareNote; internal set => Set(ref _hardwareNote, value); }
    // Pipelines whose last run failed: "name (профиль «X»): error", joined.
    public string? PipelineError { get => _pipelineError; internal set => Set(ref _pipelineError, value); }
    // Set when the tray block or the dashboard card could not be placed into LLT's window (a private member is missing).
    public string? ShellError { get => _shellError; internal set => Set(ref _shellError, value); }
    // LLT's texts that could not be corrected at start (a resource class changed): joined, or null.
    public string? TranslationProblems { get => _translationProblems; internal set => Set(ref _translationProblems, value); }
    public DateTimeOffset? LastApplyAt { get => _lastApplyAt; internal set => Set(ref _lastApplyAt, value); }
    public bool? SelfTestOk { get => _selfTestOk; internal set => Set(ref _selfTestOk, value); }
    public IReadOnlyList<string> SelfTestFailures { get => _selfTestFailures; internal set => Set(ref _selfTestFailures, value); }

    // The last apply result of every parameter that was applied: "ok", "skipped" or the error text.
    public IReadOnlyDictionary<ParameterId, string> ParameterResults { get => _parameterResults; internal set => Set(ref _parameterResults, value); }

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        var handler = PropertyChanged;
        if (handler is null) return;
        _post(() =>
        {
            try { handler(this, new PropertyChangedEventArgs(name)); }
            catch (Exception e) { Log.Error("status change handler", e); }
        });
    }
}
