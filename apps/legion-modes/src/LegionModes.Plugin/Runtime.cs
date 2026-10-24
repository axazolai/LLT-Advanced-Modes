using Path = System.IO.Path;
using Message = LegionModes.Core.Ipc.Message;
using Timer = System.Threading.Timer;
using System.Diagnostics;
using System.IO;
using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Ipc;
using LegionModes.Core.Model;
using LegionModes.Core.Programs;
using LegionModes.Core.Quick;
using LegionModes.Core.Status;
using LegionModes.Plugin.Quick;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Features.Hybrid;
using LenovoLegionToolkit.Lib.Station.Services;
using Microsoft.Win32;

namespace LegionModes.Plugin;

public interface IApplier
{
    // one result per changed parameter
    Task<IReadOnlyDictionary<ParameterId, ParameterResult>> ApplyAsync(Decision d, IReadOnlyList<ParameterId> changed);
    // error text or null
    Task<string?> SleepNowAsync();
}

internal sealed class NullApplier : IApplier
{
    public Task<IReadOnlyDictionary<ParameterId, ParameterResult>> ApplyAsync(Decision d, IReadOnlyList<ParameterId> changed) =>
        Task.FromResult<IReadOnlyDictionary<ParameterId, ParameterResult>>(new Dictionary<ParameterId, ParameterResult>());

    public Task<string?> SleepNowAsync() => Task.FromResult<string?>(null);
}

// Local time: Decision.Reason prints times in the clock's offset.
internal sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

// A save the loader would reject; nothing was written. The message lists the errors.
public sealed class ConfigInvalidException(IReadOnlyList<string> errors)
    : Exception(Tr.F("configuration not saved: {0}", string.Join(" | ", errors)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

// The only stateful part in LLT's process. ProfileEngine is not thread-safe: every engine call (Update, OnResume, Set,
// Clear, Reconfigure) runs under _gate. WorkloadTracker is thread-safe.
public sealed partial class Runtime : IAsyncDisposable, Ui.IModesHost
{
    static volatile Runtime? _current;
    public static Runtime? Current => _current;

    static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);
    static readonly TimeSpan ScanPeriod = TimeSpan.FromSeconds(30);
    static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(10);
    static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(60);

    readonly string _dataDir;
    readonly IUiDispatcher _ui;
    readonly SystemClock _clock = new();
    readonly object _gate = new();
    readonly object _sleepGate = new();
    readonly object _changeGate = new();
    readonly EventCoalescer _coalescer;
    readonly WorkloadTracker _tracker;
    readonly LltInputs _inputs = new();
    readonly SemaphoreSlim _applyGate = new(1, 1);
    readonly MenuSync _menu = new();

    Task _startupHardware = Task.CompletedTask;
    ProfileEngine _engine;
    ModesConfig _config;
    ProgramsConfig _programs;
    ProgramClassifier _classifier;
    Decision? _decision;
    EffectiveParameters? _lastApplied;      // under _applyGate
    Workload _lastLoad = Workload.Idle;
    bool _fullPending;
    bool _resumePending;
    bool _immediate;                         // the next tick updates without waiting for the coalescing window
    bool _wasPaused;
    bool _autonomySaved;
    bool _resetCurveOptimizer;
    readonly RetryTracker _retry = new(RetryPeriod);   // under _gate; failed parameters: 3 retries, 60 s apart, per value
    Transition? _carry;                                // under _gate; profile changes the raw-input updates produced since the last tick
    readonly Dictionary<ParameterId, ParameterResult> _lastResults = new();   // under _gate
    readonly TaskCompletionSource _firstApply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _closing;
    int _sleeping;
    DateTimeOffset _lastHeartbeatLog = DateTimeOffset.MinValue;
    int _ticking;
    int _beating;
    int _scanning;
    int _disposed;
    volatile bool _hasControl;
    DateTimeOffset? _sleepAt;
    DateTimeOffset? _sleptAt;

    Timer? _tickTimer, _scanTimer, _heartbeatTimer, _sleepTimer, _changeTimer;
    PipeServer? _pipe;
    QuickServer? _quick;
    DisplayStateWatcher? _display;

    int _godModeHold;
    bool _presetRenamePending;
    int _startupRun;                         // the StartupAutomationEvent goes out once, with the first apply under control
    Pipelines.PipelineRunner? _pipes;
    Pipelines.LltEvents? _lltEvents;
    readonly Dictionary<ParameterId, string> _results = new();   // under _gate; "ok", "skipped" or an error text

    public StatusModel Status { get; }
    public Status.DeviceStatus Devices { get; }
    public Status.DgpuLive? GpuLive { get; private set; }
    public Status.LiveFacts? Facts { get; private set; }
    Shell.ShellInjector? _shell;
    Shell.LanguageWatcher? _language;
    Shell.Notifier? _notifier;
    internal Task ShellCompleted => _shell?.Completed ?? Task.CompletedTask;

    // While LLT's GodMode window is open the power parameter is not applied (the other parameters still are); ending the
    // hold re-applies everything once.
    public void BeginGodModeEdit() => Interlocked.Increment(ref _godModeHold);

    public void EndGodModeEdit()
    {
        if (Interlocked.Decrement(ref _godModeHold) > 0) return;
        Interlocked.Exchange(ref _godModeHold, 0);
        if (_presetRenamePending) { _presetRenamePending = false; _ = SyncPresetsAsync(); }
        if (!_hasControl) return;
        lock (_gate) { _lastApplied = null; _retry.Forget(ParameterId.Power); _fullPending = true; _immediate = true; }
    }

    public ModesConfig Config => _config;                           // the loaded modes.json, or the factory set while Status.ConfigError is set
    public ProgramsConfig Programs => _programs;
    public Decision? Last => _decision;
    public bool HasControl => _hasControl;
    public int MaxBrightness { get; }                               // WmiMonitorBrightness Level max, 100 when unreadable
    public DisplayStateKind DisplayState => _display?.State ?? DisplayStateKind.On;
    public event Action<Decision>? Decided;                         // raised on the UI dispatcher
    public event Action<DisplayStateKind>? DisplayStateChanged;     // raised on the UI dispatcher
    internal IApplier Applier { get; set; } = new NullApplier();

    // Self-test facts.
    internal bool QuickPipeListening => _quick is not null;
    internal bool DisplayStateRegistered => _display?.Registered == true;
    internal string? DisplayStateError => _display?.Error;
    internal bool PluginPipeListening => _pipe is not null;

    // Completes with the first apply under control (never when the plugin has no control).
    internal Task FirstApply => _firstApply.Task;

    // The latest result of every parameter that was applied.
    internal IReadOnlyDictionary<ParameterId, ParameterResult> LastResults
    {
        get { lock (_gate) return new Dictionary<ParameterId, ParameterResult>(_lastResults); }
    }

    string ModesPath => Path.Combine(_dataDir, "modes.json");
    string ProgramsPath => Path.Combine(_dataDir, "programs.json");

    // The files the UI edits and the snapshot it takes.
    public string ModesFilePath => ModesPath;
    public string ProgramsFilePath => ProgramsPath;
    public string LightingSnapshotPath => Path.Combine(_dataDir, "lighting-profiles.json");
    public string EditorLayoutPath => Path.Combine(_dataDir, "editor-layout.json");
    string ControlPath => Path.Combine(_dataDir, "control.json");

    Runtime(string dataDir, IUiDispatcher ui)
    {
        _dataDir = dataDir;
        _ui = ui;
        Status = new StatusModel(a =>
        {
            try { _ = ui.InvokeAsync(a); } catch (Exception e) { Log.Error("post to UI", e); }
        });
        Devices = new Status.DeviceStatus(a =>
        {
            try { _ = ui.InvokeAsync(a); } catch (Exception e) { Log.Error("post to UI", e); }
        });

        MaxBrightness = Apply.Brightness.Max();
        var modes = ModesStore.Load(ModesPath, MaxBrightness);
        if (modes.Notice is not null && modes.Error is null)
            Migrate(ModesPath, modes.Backup ?? "modes.schema1.json", modes.Notice, () => ModesStore.Save(ModesPath, modes.Value));
        var programs = ProgramsStore.Load(ProgramsPath);
        if (programs.Notice is not null && programs.Error is null)
            Migrate(ProgramsPath, "programs.schema1.json", programs.Notice, () => ProgramsStore.Save(ProgramsPath, programs.Value));
        Status.ConfigError = modes.Error;
        Status.ProgramsError = programs.Error;
        _config = modes.Value;
        _programs = programs.Value;
        _classifier = new ProgramClassifier(_programs.Entries);

        Apply.GodModePresets.MapPath = Path.Combine(dataDir, "preset-map.json");
        var read = ReadControlFile();
        _data = read.Data;
        if (_controlUnreadable)
        {
            // Nothing is known about the last shutdown; control.json stays as it is until a read succeeds.
            UpdateData(d => d with { Running = true });
        }
        else
        {
            var (count, reset) = ShutdownSafety.OnStart(_data.AbnormalShutdowns, !_data.Running);
            _resetCurveOptimizer = reset;
            UpdateData(d => d with { AbnormalShutdowns = count, Running = true });
        }
        _autonomySaved = _data.Autonomy;
        Status.TranslationProblems = Ui.LltTranslations.Problems.Count == 0 ? null : string.Join("; ", Ui.LltTranslations.Problems);
        Devices.RestoreSwitch(_data.CameraOff is { } off && _data.CameraAt is { } at ? new Status.CameraSwitch(off, at) : null);
        Devices.CameraSwitched += s => UpdateData(d => d with { CameraOff = s.Off, CameraAt = s.At });

        _engine = new ProfileEngine(_config, _clock, _data.Autonomy);
        _tracker = new WorkloadTracker(_clock, TimeSpan.Zero);
        _coalescer = new EventCoalescer(_clock, CoalesceWindow);
        Applier = new Apply.Applier(() => _data.Scheme, () => _config, n => Status.HardwareNote = n,
            t => Status.LastApplyError = t, () => _hasControl && _decision is { Paused: false });
        if (_data.HasControl)
        {
            _hasControl = true;
            Status.HasControl = true;
        }
    }

    // control.json: a missing file is the empty record; an unreadable one is copied to control.unreadable.json (an existing copy
    // is kept), read once more, and if it still fails the error shows on Status and nothing is written over it.
    ControlRead ReadControlFile()
    {
        var read = ControlFile.Read(ControlPath);
        if (read.Error is null) return read;
        Log.Error("read control.json", new InvalidOperationException(read.Error));
        try
        {
            var copy = Path.Combine(_dataDir, "control.unreadable.json");
            if (!File.Exists(copy)) File.Copy(ControlPath, copy, overwrite: false);
        }
        catch (Exception e) { Log.Error("keep a copy of the unreadable control.json", e); }
        Thread.Sleep(250);
        read = ControlFile.Read(ControlPath);
        if (read.Error is null) return read;
        _controlUnreadable = true;
        Status.ControlError = read.Error;
        return read;
    }

    // A file of the old schema is renamed and the new content saved in its place.
    void Migrate(string path, string backupName, string notice, Action save)
    {
        try
        {
            File.Copy(path, Path.Combine(_dataDir, backupName), overwrite: true);   // the old file stays until the new one replaces it
            save();
            Log.Trace(notice);
        }
        catch (Exception e) { Log.Error("migrate " + path, e); }
    }

    public static async Task<Runtime> StartAsync(string dataDir, IUiDispatcher ui)
    {
        var rt = new Runtime(dataDir, ui);
        try
        {
            await rt.InitAsync().ConfigureAwait(false);
        }
        catch
        {
            await rt.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _current = rt;
        rt.StartShell(ui);
        rt.WatchExit();
        _ = Task.Run(rt.ReportStartAsync);
        return rt;
    }

    // The tray block, the dashboard card, the notifications and the device status; none of them can stop the runtime.
    void StartShell(IUiDispatcher ui)
    {
        try { Devices.Start(); }
        catch (Exception e) { Log.Error("device status start", e); }
        if (Applier is Apply.Applier)
        {
            try { GpuLive = new Status.DgpuLive(); GpuLive.Start(); }
            catch (Exception e) { Log.Error("dGPU live start", e); GpuLive = null; }
            try { Facts = new Status.LiveFacts(); Facts.Start(); }
            catch (Exception e) { Log.Error("live facts start", e); Facts = null; }
        }
        try { Devices.PropertyChanged += (_, e) => { if (e.PropertyName == "MicMuted") SyncMenu(); }; }
        catch (Exception e) { Log.Error("device status menu hook", e); }
        try { _notifier = new Shell.Notifier(this, _dataDir); _notifier.Start(); }
        catch (Exception e) { Log.Error("notifier start", e); }
        try { _shell = new Shell.ShellInjector(this, ui); _shell.Start(); }
        catch (Exception e) { Log.Error("shell start", e); }
        try { Tr.Changed += OnLanguageChanged; _language = new Shell.LanguageWatcher(); }
        catch (Exception e) { Log.Error("language watcher start", e); }
    }

    async Task InitAsync()
    {
        _inputs.Changed += OnRawInput;
        _inputs.Resumed += () => { lock (_gate) { _resumePending = true; _coalescer.Signal(true); } };
        _inputs.PowerModeChanged += state => { if (_hasControl && state != PowerModeState.GodMode) Signal(true); };
        _inputs.ProcessStarted += OnProcessStarted;
        _inputs.ProcessStopped += pid => _tracker.Stopped(pid);
        await _inputs.StartAsync().ConfigureAwait(false);

        try { Resync(); } catch (Exception e) { Log.Error("first process scan", e); }

        if (_hasControl)
        {
            try { await AssertControlAsync(_data).ConfigureAwait(false); }
            catch (Exception e) { Log.Error("re-assert control at start", e); }
        }

        try
        {
            _pipe = new PipeServer(Paths.PluginPipe, HandlePipeAsync);
            _pipe.Start();
        }
        catch (Exception e)
        {
            Log.Error("plugin pipe", e);
            _pipe = null;
        }
        _quick = QuickServer.TryStart(Quick);

        // Pipelines: nothing runs without control or while paused; LLT's events reach the in-force profile's winners.
        if (Applier is Apply.Applier)
        {
            try
            {
                _pipes = new Pipelines.PipelineRunner(() => _hasControl && _decision is { Paused: false },
                    p => IoCContainer.Resolve<LenovoLegionToolkit.Lib.Automation.AutomationProcessor>().RunNowAsync(p),
                    text => Status.PipelineError = text);
                _lltEvents = new Pipelines.LltEvents(OnLltEventAsync);
                await _lltEvents.UpdateAsync(_config).ConfigureAwait(false);
            }
            catch (Exception e) { Log.Error("pipeline start", e); }
        }

        _display = await DisplayStateWatcher.StartAsync(_ui).ConfigureAwait(false);
        _display.Changed += OnDisplayState;
        if (Applier is Apply.Applier hardware)
        {
            hardware.Start();                                            // the dGPU attach hook
            DisplayStateChanged += hardware.Idle.OnDisplayState;         // keyboard, logo and ports follow the display
        }

        // The hardware's GPU mode is read before the first decision: discrete-only mode means the dGPU override.
        if (Applier is Apply.Applier)
        {
            try
            {
                var hybrid = IoCContainer.Resolve<HybridModeFeature>();
                if (await hybrid.IsSupportedAsync().ConfigureAwait(false) && await hybrid.GetStateAsync().ConfigureAwait(false) == HybridModeState.Off)
                {
                    lock (_gate) _engine.Set(OverrideKind.DGpu);
                    Status.HardwareNote = Apply.Applier.DGpuHeld;
                }
            }
            catch (Exception e) { Log.Error("read GPU mode at start", e); }
        }
        // Preset sync (while in control), the first lighting snapshot and the Curve Optimizer safety reset; the first apply
        // waits for this task.
        _startupHardware = Task.Run(StartupHardwareAsync);

        Signal(true, immediate: true);
        _sleepTimer = new Timer(_ => OnSleepTimer(), null, Timeout.Infinite, Timeout.Infinite);
        _changeTimer = new Timer(_ => OnChangeTimer(), null, Timeout.Infinite, Timeout.Infinite);
        _tickTimer = new Timer(_ => _ = TickAsync(), null, TickPeriod, TickPeriod);
        _scanTimer = new Timer(_ => _ = ScanAsync(), null, ScanPeriod, ScanPeriod);
        _heartbeatTimer = new Timer(_ => _ = HeartbeatAsync(), null, TimeSpan.Zero, HeartbeatPeriod);
    }

    // Runs once per start, before the first apply. Only with the real applier (a test applier is not a hardware session).
    async Task StartupHardwareAsync()
    {
        if (Applier is not Apply.Applier) return;
        try { await Backlight.LightingStore.SnapshotIfMissingAsync(Path.Combine(_dataDir, "lighting-profiles.json")).ConfigureAwait(false); }
        catch (Exception e) { Log.Error("lighting snapshot at start", e); }
        await SyncPresetsAsync().ConfigureAwait(false);   // in or out of control: names and the map follow modes.json
        if (_hasControl) await SyncWakeOverridesAsync().ConfigureAwait(false);
        if (_resetCurveOptimizer) await ResetCurveOptimizerAsync().ConfigureAwait(false);
    }

    // Three abnormal shutdowns in a row: every plugin preset goes back to Curve Optimizer 0. The counter was reset when the
    // reset was decided; a failed reset puts it one short of the threshold so the next abnormal start tries again.
    async Task ResetCurveOptimizerAsync()
    {
        try
        {
            await Apply.CurveOptimizerReset.RunAsync().ConfigureAwait(false);
            Log.Trace("Curve Optimizer reset after repeated abnormal shutdowns");
            Shell.Notifier.Show(Tr.T("Curve Optimizer reset to 0 after three crashes in a row"));
        }
        catch (Exception e)
        {
            Log.Error("Curve Optimizer reset", e);
            Status.LastApplyError = "Curve Optimizer reset: " + e.Message;
            UpdateData(d => d with { AbnormalShutdowns = ShutdownSafety.Threshold - 1 });
        }
    }

    // The plugin's GodMode presets match the profiles of modes.json through preset-map.json. Called at start, at Take and after
    // every config save, in or out of control, and before the editor opens LLT's GodMode window. A failure shows on Status and
    // is returned.
    public async Task<string?> SyncPresetsAsync()
    {
        if (Applier is not Apply.Applier) return null;
        try
        {
            ModesConfig config;
            lock (_gate) config = _config;
            // Presets are deleted only for a modes.json that loaded: a broken file means the factory set stands in.
            await Apply.GodModePresets.SyncAsync(config, prune: Status.ConfigError is null).ConfigureAwait(false);
            return null;
        }
        catch (Exception e)
        {
            Log.Error("GodMode preset sync", e);
            Status.LastApplyError = "preset sync: " + e.Message;
            return e.Message;
        }
    }

    // ---- inputs -------------------------------------------------------------------------------------------------

    void Signal(bool full, bool immediate = false)
    {
        lock (_gate)
        {
            _coalescer.Signal(full);
            if (immediate) _immediate = true;
        }
    }

    // The command line comes from WMI; the process may be gone by then, and the path is then read directly.
    void OnProcessStarted(int pid, string name)
    {
        _ = Task.Run(() =>
        {
            try
            {
                ProgramClassifier classifier;
                lock (_gate) classifier = _classifier;
                var info = ProcessScanner.InfoOf(pid);
                var path = info?.Path ?? ProcessScanner.PathOf(pid);
                var cls = classifier.Classify(path, name, info?.CommandLine);
                if (cls is { } c) _tracker.Started(pid, c);
            }
            catch (Exception e) { Log.Error("process start", e); }
        });
    }

    // A source, lid or monitor change reaches the engine at once: holds, overrides and autonomy lift on every change, also when
    // the burst returns to where it began. Only the apply waits for the coalescing window, on the last decision.
    void OnRawInput()
    {
        lock (_gate)
        {
            try
            {
                var d = _engine.Update(new Inputs(_inputs.Source, _inputs.Lid, _tracker.Current, _inputs.ExternalDisplay));
                _carry = Transition.Chain(_carry, d.Transition);
            }
            catch (Exception e) { Log.Error("raw input update", e); }
            _coalescer.Signal(false);
        }
    }

    // Every 30 s: processes, and also source, lid and external displays, in case an event was missed.
    async Task ScanAsync()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        try
        {
            await Task.Run(Resync).ConfigureAwait(false);
            await _inputs.RefreshAsync().ConfigureAwait(false);
            Signal(false);
        }
        catch (Exception e) { Log.Error("periodic scan", e); }
    }

    void Resync()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            ProgramClassifier classifier;
            lock (_gate) classifier = _classifier;
            _tracker.Resync(ProcessScanner.Scan(classifier));
        }
        finally { Volatile.Write(ref _scanning, 0); }
    }

    void OnDisplayState(DisplayStateKind state)
    {
        try
        {
            var handlers = DisplayStateChanged;
            if (handlers is null) return;
            foreach (var h in handlers.GetInvocationList())
            {
                try { ((Action<DisplayStateKind>)h)(state); }
                catch (Exception e) { Log.Error("DisplayStateChanged handler", e); }
            }
        }
        catch (Exception e) { Log.Error("display state", e); }
    }

    // ---- engine tick --------------------------------------------------------------------------------------------

    async Task TickAsync()
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            Decision d;
            bool full, autonomy;
            lock (_gate)
            {
                var load = _tracker.Current;
                if (load != _lastLoad) { _lastLoad = load; _coalescer.Signal(false); }
                var due = _coalescer.TryTake(out full);
                if (!due && !_immediate) return;
                _immediate = false;
                var inputs = new Inputs(_inputs.Source, _inputs.Lid, load, _inputs.ExternalDisplay);
                if (_resumePending)
                {
                    _resumePending = false;
                    _engine.OnResume(inputs);
                    full = true;
                }
                d = _engine.Update(inputs);
                // Profile changes the raw-input updates made since the last tick join this one: a burst that ends where it began
                // changes nothing, any other burst runs one «выход» and one «вход».
                var transition = Transition.Chain(_carry, d.Transition);
                _carry = null;
                if (!Equals(transition, d.Transition)) d = d with { Transition = transition };
                autonomy = _engine.Autonomy;
                full |= _fullPending;
                _fullPending = false;
            }
            PersistAutonomy(autonomy);
            Publish(d);
            ScheduleSleep(d.ForcedSleepAt);
            ArmChange(d.NextChangeAt);
            await ApplyAsync(d, full).ConfigureAwait(false);
        }
        catch (Exception e) { Log.Error("engine tick", e); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    // control.json follows the autonomy override (a restart on battery restores it; the first update on AC lifts it).
    void PersistAutonomy(bool autonomy)
    {
        if (autonomy == _autonomySaved) return;
        _autonomySaved = autonomy;
        UpdateData(d => d with { Autonomy = autonomy });
    }

    void Publish(Decision d)
    {
        var previous = _decision;
        _decision = d;
        Status.Source = d.Axes.Source;
        Status.Lid = d.Axes.Lid;
        Status.Load = d.Axes.Load;
        Status.ExternalDisplay = d.Axes.Monitor == MonitorState.Connected;
        if (previous is not null && Same(previous, d)) return;
        Status.Decision = d;
        SyncMenu();
        try
        {
            _ = _ui.InvokeAsync(() =>
            {
                var handlers = Decided;
                if (handlers is null) return;
                foreach (var h in handlers.GetInvocationList())
                {
                    try { ((Action<Decision>)h)(d); }
                    catch (Exception e) { Log.Error("Decided handler", e); }
                }
            });
        }
        catch (Exception e) { Log.Error("post Decided", e); }
    }

    static bool Same(Decision a, Decision b) =>
        a.Profile.Id == b.Profile.Id && a.Reason == b.Reason && a.Axes == b.Axes && a.Paused == b.Paused && a.DGpu == b.DGpu
        && a.ForcedSleepAt == b.ForcedSleepAt && a.Notes.SequenceEqual(b.Notes)
        && a.Overrides.Select(o => (o.Kind, o.Workload)).SequenceEqual(b.Overrides.Select(o => (o.Kind, o.Workload)))
        && a.Pending.Select(p => (p.Profile.Id, p.At)).SequenceEqual(b.Pending.Select(p => (p.Profile.Id, p.At)))
        && ParameterDiff.Changed(a.Parameters, b.Parameters, false).Count == 0;

    // A language change: the desktop menu is rewritten in the new language and the GodMode presets take the profile names of it.
    void OnLanguageChanged()
    {
        SyncMenu();
        if (Volatile.Read(ref _godModeHold) > 0) _presetRenamePending = true;   // LLT's GodMode window is open: its save would overwrite the rename
        else _ = SyncPresetsAsync();
    }

    // After every decision change and when the microphone state changes.
    void SyncMenu()
    {
        var d = _decision;
        if (d is null) return;
        bool Has(OverrideKind k) => d.Overrides.Any(o => o.Kind == k);
        _menu.Update(new MenuState(d.Axes.Source == PowerSource.Battery, Has(OverrideKind.Autonomy),
            d.Overrides.FirstOrDefault(o => o.Kind == OverrideKind.ForceWorkload)?.Workload, Has(OverrideKind.StayAwake),
            Has(OverrideKind.RapidCharge), Has(OverrideKind.ChargeFull), d.Paused, Devices.MicMuted == true));
    }

    async Task ApplyAsync(Decision d, bool full)
    {
        if (!_hasControl) { _wasPaused = d.Paused; return; }
        if (_wasPaused && !d.Paused) full = true;
        _wasPaused = d.Paused;
        if (d.Paused) return;
        await _startupHardware.ConfigureAwait(false);   // never faults: the task catches
        var pipes = _pipes;
        if (pipes is null) { await ApplyCoreAsync(d, full).ConfigureAwait(false); return; }
        ModesConfig config;
        lock (_gate) config = _config;
        try
        {
            // «выход» of the old profile (awaited at most 10 s), the parameters, «вход» of the new one (queued, not awaited).
            if (d.Transition is { } t) await pipes.OnTransitionAsync(config, t, () => ApplyCoreAsync(d, full)).ConfigureAwait(false);
            else await ApplyCoreAsync(d, full).ConfigureAwait(false);
            if (d.Transition is { From: not null } changed) Shell.Notifier.ShowProfile(ProfileNames.Display(changed.To));
            if (Interlocked.Exchange(ref _startupRun, 1) == 0) _ = pipes.OnStartAsync(config, d.Profile);
        }
        catch (Exception e) { Log.Error("pipelines", e); }
    }

    // LLT's events: the pipelines of the profile in force that win and match run, in list order.
    async Task OnLltEventAsync(LenovoLegionToolkit.Lib.Automation.IAutomationEvent e)
    {
        var pipes = _pipes;
        var d = _decision;
        if (pipes is null || d is null || !_hasControl || d.Paused) return;
        ModesConfig config;
        lock (_gate) config = _config;
        await pipes.OnEventAsync(config, d.Profile, e).ConfigureAwait(false);
    }

    async Task ApplyCoreAsync(Decision d, bool full)
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_hasControl) return;
            var changed = ParameterDiff.Changed(_lastApplied, d.Parameters, full).ToList();
            lock (_gate)
            {
                // a failed parameter is retried 3 times, RetryPeriod apart, while its value stays the same
                foreach (var f in _retry.Due(_clock.Now, id => ParameterDiff.ValueOf(d.Parameters, id)))
                    if (!changed.Contains(f)) changed.Add(f);
            }
            if (Volatile.Read(ref _godModeHold) > 0 && changed.Remove(ParameterId.Power))
            {
                lock (_gate)
                {
                    _retry.Forget(ParameterId.Power);
                    _results[ParameterId.Power] = "skipped";
                    Status.ParameterResults = new Dictionary<ParameterId, string>(_results);
                }
            }
            if (changed.Count == 0) return;
            IReadOnlyDictionary<ParameterId, ParameterResult> result;
            try
            {
                result = await Applier.ApplyAsync(d, changed).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error("apply", e);
                result = changed.ToDictionary(p => p, p => ParameterResult.Fault(e.GetType().Name + ": " + e.Message));
            }
            // _lastApplied advances; a parameter that failed is retried by _retry (3 times, then only when its value changes)
            _lastApplied = d.Parameters;
            string[] errors;
            lock (_gate)
            {
                foreach (var p in changed)
                {
                    var r = result.TryGetValue(p, out var found) ? found : ParameterResult.Ok;
                    _retry.Record(p, ParameterDiff.ValueOf(d.Parameters, p), r.Failed, _clock.Now);
                    _lastResults[p] = r;
                    _results[p] = r.Kind switch
                    {
                        ResultKind.Ok => "ok",
                        ResultKind.Note => StatusModel.NotePrefix + r.Text,
                        _ => r.Text ?? Tr.T("error"),
                    };
                }
                Status.ParameterResults = new Dictionary<ParameterId, string>(_results);
                errors = changed.Where(p => _lastResults[p].Failed).Select(p => $"{p}: {_lastResults[p].Text}").ToArray();
            }
            Status.LastApplyError = errors.Length > 0 ? string.Join("; ", errors) : null;
            Status.LastApplyAt = _clock.Now;
        }
        finally
        {
            _applyGate.Release();
            if (_hasControl) _firstApply.TrySetResult();
        }
    }

    // ---- timers at the engine's own instants ----------------------------------------------------------------------

    // The earliest delay end, hold end or pause end of the last decision: the engine is asked again then.
    void ArmChange(DateTimeOffset? at)
    {
        lock (_changeGate)
        {
            if (_changeTimer is null) return;
            if (at is not { } t) { _changeTimer.Change(Timeout.Infinite, Timeout.Infinite); return; }
            var due = t - _clock.Now + TimeSpan.FromMilliseconds(50);
            if (due < TimeSpan.Zero) due = TimeSpan.Zero;
            if (due > TimeSpan.FromDays(30)) due = TimeSpan.FromDays(30);
            _changeTimer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    void OnChangeTimer()
    {
        try { Signal(false, immediate: true); }
        catch (Exception e) { Log.Error("change timer", e); }
    }

    // ---- forced sleep -------------------------------------------------------------------------------------------

    void ScheduleSleep(DateTimeOffset? at)
    {
        lock (_sleepGate)
        {
            if (at == _sleepAt) return;
            _sleepAt = at;
            ArmSleepTimer();
        }
    }

    void RescheduleSleep()
    {
        lock (_sleepGate)
        {
            _sleepAt = _decision?.ForcedSleepAt;
            _sleptAt = null;
            ArmSleepTimer();
        }
    }

    // _sleepGate held. A time already past fires at once.
    void ArmSleepTimer()
    {
        if (_sleepTimer is null) return;
        if (_sleepAt is not { } at || at == _sleptAt) { _sleepTimer.Change(Timeout.Infinite, Timeout.Infinite); return; }
        var due = at - _clock.Now;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        if (due > TimeSpan.FromDays(30)) due = TimeSpan.FromDays(30);
        _sleepTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    async void OnSleepTimer()
    {
        try
        {
            DateTimeOffset at;
            lock (_sleepGate)
            {
                if (_sleepAt is not { } a) return;
                at = a;
                var left = at - _clock.Now;
                if (left > TimeSpan.FromMilliseconds(50)) { _sleepTimer?.Change(left, Timeout.InfiniteTimeSpan); return; }   // woke early
                if (!_hasControl) return;
                _sleptAt = at;
            }
            Log.Trace("forced sleep: lid closed past its limit");
            Interlocked.Increment(ref _sleeping);
            try
            {
                var error = await Applier.SleepNowAsync().ConfigureAwait(false);
                if (error is not null) Status.LastApplyError = "sleep: " + error;
            }
            finally { Interlocked.Decrement(ref _sleeping); }
        }
        catch (Exception e) { Log.Error("forced sleep", e); }
    }

    // ---- configuration ------------------------------------------------------------------------------------------

    // Validates first; an invalid config is refused with the reasons and nothing is written. The first save over a file that
    // failed to load keeps a copy of it.
    public Task SaveModesAsync(ModesConfig c) => SaveModesCoreAsync(c, factory: false);

    // Profiles to factory: modes.json, the presets synced to it and given their factory values, then one full apply (the
    // engine sees the new profiles only after the presets hold the new values). A preset failure is thrown after all that.
    public Task ResetModesToFactoryAsync() => SaveModesCoreAsync(FactoryProfiles.Create(), factory: true);

    async Task SaveModesCoreAsync(ModesConfig c, bool factory)
    {
        var errors = ModesValidator.Errors(c, MaxBrightness).ToArray();
        if (errors.Length > 0) throw new ConfigInvalidException(errors);
        var hadError = Status.ConfigError is not null;
        await Task.Run(() =>
        {
            if (hadError) KeepBrokenCopy(ModesPath);
            ModesStore.Save(ModesPath, c);
        }).ConfigureAwait(false);
        // The presets exist before the engine can apply one of them; a rename moves the preset, in or out of control.
        lock (_gate) _config = c;
        await SyncPresetsAsync().ConfigureAwait(false);
        string? presetError = null;
        if (factory && Applier is Apply.Applier)
        {
            try { await Apply.GodModePresets.ResetToFactoryAsync(c).ConfigureAwait(false); }
            catch (Exception e)
            {
                Log.Error("GodMode preset reset", e);
                presetError = e.Message;
            }
        }
        lock (_gate)
        {
            _engine.Reconfigure(c);
            _immediate = true;
            if (factory) _coalescer.Signal(true);
        }
        Status.ConfigError = null;
        if (_lltEvents is { } events) await events.UpdateAsync(c).ConfigureAwait(false);
        if (presetError is not null) throw new InvalidOperationException(Tr.F("GodMode presets: {0}", presetError));
    }

    public async Task SavePrograms(ProgramsConfig c)
    {
        var errors = ProgramsStore.Errors(c);
        if (errors.Count > 0) throw new ConfigInvalidException(errors);
        var hadError = Status.ProgramsError is not null;
        await Task.Run(() =>
        {
            if (hadError) KeepBrokenCopy(ProgramsPath);
            ProgramsStore.Save(ProgramsPath, c);
        }).ConfigureAwait(false);
        lock (_gate) { _programs = c; _classifier = new ProgramClassifier(c.Entries); }
        Status.ProgramsError = null;
        await Task.Run(Resync).ConfigureAwait(false);
        Signal(false, immediate: true);
        if (_hasControl) await SyncWakeOverridesAsync().ConfigureAwait(false);
    }

    static void KeepBrokenCopy(string path)
    {
        if (File.Exists(path)) File.Copy(path, $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
    }

    // ---- supervision --------------------------------------------------------------------------------------------

    async Task HeartbeatAsync()
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _beating, 1) == 1) return;
        try
        {
            var reply = await PipeClient.SendAsync(Paths.GuardPipe,
                new Heartbeat(_clock.Now, _inputs.Lid == LidState.Open, _inputs.ExternalDisplay, _hasControl),
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (reply is not Ack { Ok: true }) LogHeartbeatFailure(new InvalidOperationException("the guard did not acknowledge the heartbeat"));
        }
        catch (Exception e) { LogHeartbeatFailure(e); }
        finally { Volatile.Write(ref _beating, 0); }
    }

    // At most once a minute: a stopped guard would otherwise fill the log every 10 s.
    void LogHeartbeatFailure(Exception e)
    {
        var now = _clock.Now;
        if (now - _lastHeartbeatLog < TimeSpan.FromMinutes(1)) return;
        _lastHeartbeatLog = now;
        Log.Error("heartbeat", e);
    }

    async Task ReportStartAsync()
    {
        try
        {
            var llt = "unknown";
            try { llt = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? Paths.LltExe).FileVersion ?? llt; }
            catch (Exception e) { Log.Error("read LLT version", e); }
            var plugin = typeof(Runtime).Assembly.GetName().Version?.ToString() ?? "unknown";
            await PipeClient.SendAsync(Paths.KeeperPipe, new Loaded(llt, plugin), TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var report = await SelfTest.RunAsync().ConfigureAwait(false);
            Status.SelfTestFailures = report.Failures;
            Status.SelfTestOk = report.Ok;
            if (!report.Ok) Log.Error("self-test failed: " + string.Join(" | ", report.Failures), new InvalidOperationException("self-test"));
            await PipeClient.SendAsync(Paths.KeeperPipe, report, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception e) { Log.Error("start report", e); }
    }

    Task<Message> HandlePipeAsync(Message m)
    {
        try
        {
            if (m is ExitRequest)
            {
                _ = CloseLltAsync();
                return Task.FromResult<Message>(new Ack(true));
            }
            return Task.FromResult<Message>(new Ack(false, "unsupported message"));
        }
        catch (Exception e)
        {
            Log.Error("plugin pipe handler", e);
            return Task.FromResult<Message>(new Ack(false, e.Message));
        }
    }

    // The call LLT's tray "Close" item makes (TrayHelper.cs: await App.Current.ShutdownAsync() on the UI thread). It runs
    // ExtensionManager.StopAsync, so Provider.DisposeAsync follows. Not awaited by the pipe handler; the short delay
    // lets the Ack reach the caller before the server is disposed.
    async Task CloseLltAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1) return;   // at most once
        try
        {
            await Task.Delay(300).ConfigureAwait(false);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) { Log.Error("close LLT", new InvalidOperationException("no UI dispatcher")); return; }
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await LenovoLegionToolkit.WPF.App.Current.ShutdownAsync(); }
                catch (Exception e) { Log.Error("LLT shutdown", e); }
            });
        }
        catch (Exception e) { Log.Error("close LLT", e); }
    }

    // ---- clean-exit marker ----------------------------------------------------------------------------------------

    // Running = false on LLT's normal exit, on the session ending and (by the provider) before GracefulExit is sent.
    void WatchExit()
    {
        try { SystemEvents.SessionEnding += OnSessionEnding; }
        catch (Exception e) { Log.Error("session ending hook", e); }
        try
        {
            _ = _ui.InvokeAsync(() =>
            {
                try
                {
                    if (System.Windows.Application.Current is { } app) app.Exit += OnAppExit;
                }
                catch (Exception e) { Log.Error("application exit hook", e); }
            });
        }
        catch (Exception e) { Log.Error("application exit hook", e); }
    }

    void OnSessionEnding(object? sender, SessionEndingEventArgs e) => MarkCleanExit();

    void OnAppExit(object? sender, System.Windows.ExitEventArgs e) => MarkCleanExit();

    public void MarkCleanExit()
    {
        try { UpdateData(d => d with { Running = false }); }
        catch (Exception e) { Log.Error("clean-exit marker", e); }
    }

    // ---- disposal -----------------------------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (ReferenceEquals(_current, this)) _current = null;
        try { SystemEvents.SessionEnding -= OnSessionEnding; } catch (Exception e) { Log.Error("session ending unhook", e); }
        foreach (var t in new[] { _tickTimer, _scanTimer, _heartbeatTimer, _sleepTimer, _changeTimer })
        {
            try { t?.Dispose(); } catch (Exception e) { Log.Error("dispose timer", e); }
        }
        // Timers are stopped; wait (bounded) for a tick, an apply and a forced sleep that are already running.
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while ((Volatile.Read(ref _ticking) == 1 || Volatile.Read(ref _sleeping) > 0) && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20).ConfigureAwait(false);
            if (await _applyGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)) _applyGate.Release();
        }
        catch (Exception e) { Log.Error("wait for in-flight work", e); }
        try { Tr.Changed -= OnLanguageChanged; _language?.Dispose(); _shell?.Dispose(); _notifier?.Dispose(); Devices.Dispose(); GpuLive?.Dispose(); Facts?.Dispose(); } catch (Exception e) { Log.Error("dispose shell", e); }
        try { if (_lltEvents is { } events) await events.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { Log.Error("dispose LLT events", e); }
        try { _display?.Dispose(); } catch (Exception e) { Log.Error("dispose display state", e); }
        try { (Applier as Apply.Applier)?.Stop(); } catch (Exception e) { Log.Error("dispose applier", e); }
        try { await _inputs.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { Log.Error("dispose inputs", e); }
        if (_pipe is not null)
        {
            try { await _pipe.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { Log.Error("dispose pipe", e); }
        }
        if (_quick is not null)
        {
            try { await _quick.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { Log.Error("dispose quick pipe", e); }
        }
    }
}
