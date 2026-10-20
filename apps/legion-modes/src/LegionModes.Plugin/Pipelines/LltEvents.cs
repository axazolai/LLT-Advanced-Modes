using LegionModes.Core.Model;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.AutoListeners;
using LenovoLegionToolkit.Lib.Automation;
using LenovoLegionToolkit.Lib.Automation.Pipeline.Triggers;
using LenovoLegionToolkit.Lib.Controllers.GodMode;
using LenovoLegionToolkit.Lib.Listeners;

namespace LegionModes.Plugin.Pipelines;

// LLT's automation events, built as AutomationProcessor.cs:252-322 builds them (LLT's own automation is off while the
// plugin has control, so nobody else raises them). The six always-available listeners are subscribed at once; the six
// auto listeners only while a profile holds a pipeline whose trigger needs them. Nothing here throws into LLT.
internal sealed class LltEvents(Func<IAutomationEvent, Task> sink) : IAsyncDisposable
{
    [Flags]
    internal enum Need { None = 0, Game = 1, Process = 2, Time = 4, Inactivity = 8, WiFi = 16, Battery = 32 }

    readonly SemaphoreSlim _gate = new(1, 1);
    DisplayConfigurationListener? _display;
    NativeWindowsMessageListener? _native;
    PowerStateListener? _power;
    PowerModeListener? _mode;
    GodModeController? _god;
    SessionLockUnlockListener? _session;
    bool _started;
    Need _subscribed;
    int _disposed;

    // Which auto listeners these triggers (sub-triggers of And / Or included) need.
    internal static Need NeedsOf(IEnumerable<IAutomationPipelineTrigger> triggers)
    {
        var need = Need.None;
        foreach (var t in triggers.SelectMany(Expand))
        {
            if (t is IGameAutomationPipelineTrigger) need |= Need.Game;
            if (t is IProcessesAutomationPipelineTrigger) need |= Need.Process;
            if (t is ITimeAutomationPipelineTrigger or ITimeRangeAutomationPipelineTrigger or IPeriodicAutomationPipelineTrigger) need |= Need.Time;
            if (t is IUserInactivityPipelineTrigger) need |= Need.Inactivity;
            if (t is IWiFiConnectedPipelineTrigger or WiFiDisconnectedAutomationPipelineTrigger) need |= Need.WiFi;
            if (t is IBatteryPercentageAutomationPipelineTrigger) need |= Need.Battery;
        }
        return need;
    }

    static IEnumerable<IAutomationPipelineTrigger> Expand(IAutomationPipelineTrigger t) =>
        t is ICompositeAutomationPipelineTrigger c ? c.Triggers.SelectMany(Expand).Prepend(t) : [t];

    // The triggers of every pipeline of every profile; a pipeline that does not parse is skipped.
    internal static Need NeedsOf(ModesConfig c)
    {
        var triggers = new List<IAutomationPipelineTrigger>();
        foreach (var pipe in c.Profiles.SelectMany(p => p.Pipelines))
        {
            if (pipe.Key == PipelineKeys.Enter || pipe.Key == PipelineKeys.Exit) continue;
            try { if (PipelineJson.Read(pipe.Llt).Trigger is { } t) triggers.Add(t); }
            catch (Exception e) { Log.Error("read pipeline " + pipe.Id, e); }
        }
        return NeedsOf(triggers);
    }

    // Subscribes the fixed listeners on the first call; later calls re-evaluate the auto listeners for the config.
    public async Task UpdateAsync(ModesConfig c)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            if (!_started)
            {
                _started = true;
                _display = IoCContainer.Resolve<DisplayConfigurationListener>();
                _native = IoCContainer.Resolve<NativeWindowsMessageListener>();
                _power = IoCContainer.Resolve<PowerStateListener>();
                _mode = IoCContainer.Resolve<PowerModeListener>();
                _god = IoCContainer.Resolve<GodModeController>();
                _session = IoCContainer.Resolve<SessionLockUnlockListener>();
                _display.Changed += OnDisplay;
                _native.Changed += OnNative;
                _power.Changed += OnPower;
                _mode.Changed += OnMode;
                _god.PresetChanged += OnPreset;
                _session.Changed += OnSession;
            }
            await SetAutoAsync(NeedsOf(c)).ConfigureAwait(false);
        }
        catch (Exception e) { Log.Error("LLT event subscriptions", e); }
        finally { _gate.Release(); }
    }

    async Task SetAutoAsync(Need want)
    {
        await Toggle(Need.Game, want, () => IoCContainer.Resolve<GameAutoListener>(), (EventHandler<GameAutoListener.ChangedEventArgs>)OnGame).ConfigureAwait(false);
        await Toggle(Need.Process, want, () => IoCContainer.Resolve<ProcessAutoListener>(), (EventHandler<ProcessAutoListener.ChangedEventArgs>)OnProcess).ConfigureAwait(false);
        await Toggle(Need.Time, want, () => IoCContainer.Resolve<TimeAutoListener>(), (EventHandler<TimeAutoListener.ChangedEventArgs>)OnTime).ConfigureAwait(false);
        await Toggle(Need.Inactivity, want, () => IoCContainer.Resolve<UserInactivityAutoListener>(), (EventHandler<UserInactivityAutoListener.ChangedEventArgs>)OnInactivity).ConfigureAwait(false);
        await Toggle(Need.WiFi, want, () => IoCContainer.Resolve<WiFiAutoListener>(), (EventHandler<WiFiAutoListener.ChangedEventArgs>)OnWiFi).ConfigureAwait(false);
        await Toggle(Need.Battery, want, () => IoCContainer.Resolve<BatteryAutoListener>(), (EventHandler<BatteryAutoListener.ChangedEventArgs>)OnBattery).ConfigureAwait(false);
    }

    async Task Toggle<T>(Need flag, Need want, Func<AbstractAutoListener<T>> listener, EventHandler<T> handler) where T : EventArgs
    {
        var has = (_subscribed & flag) != 0;
        var needs = (want & flag) != 0;
        if (has == needs) return;
        try
        {
            if (needs) await listener().SubscribeChangedAsync(handler).ConfigureAwait(false);
            else await listener().UnsubscribeChangedAsync(handler).ConfigureAwait(false);
            _subscribed = needs ? _subscribed | flag : _subscribed & ~flag;
        }
        catch (Exception e) { Log.Error($"auto listener {flag}", e); }
    }

    async void Raise(Func<IAutomationEvent> make)
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            await sink(make()).ConfigureAwait(false);
        }
        catch (Exception e) { Log.Error("LLT event", e); }
    }

    void OnDisplay(object? s, DisplayConfigurationListener.ChangedEventArgs a) => Raise(() => new HDRAutomationEvent(a.HDR));
    void OnNative(object? s, NativeWindowsMessageListener.ChangedEventArgs a) => Raise(() => new NativeWindowsMessageEvent(a.Message, a.Data));
    void OnPower(object? s, PowerStateListener.ChangedEventArgs a) => Raise(() => new PowerStateAutomationEvent(a.PowerStateEvent, a.PowerAdapterStateChanged));
    void OnMode(object? s, PowerModeListener.ChangedEventArgs a) => Raise(() => new PowerModeAutomationEvent(a.State));
    void OnPreset(object? s, Guid id) => Raise(() => new CustomModePresetAutomationEvent(id));
    void OnSession(object? s, SessionLockUnlockListener.ChangedEventArgs a) => Raise(() => new SessionLockUnlockAutomationEvent(a.Locked));
    void OnGame(object? s, GameAutoListener.ChangedEventArgs a) => Raise(() => new GameAutomationEvent(a.Running));
    void OnProcess(object? s, ProcessAutoListener.ChangedEventArgs a) => Raise(() => new ProcessAutomationEvent(a.Type, a.ProcessInfo));
    void OnTime(object? s, TimeAutoListener.ChangedEventArgs a) => Raise(() => new TimeAutomationEvent(a.Time, a.Day));
    void OnInactivity(object? s, UserInactivityAutoListener.ChangedEventArgs a) => Raise(() => new UserInactivityAutomationEvent(a.TimerResolution * a.TickCount));
    void OnWiFi(object? s, WiFiAutoListener.ChangedEventArgs a) => Raise(() => new WiFiAutomationEvent(a.IsConnected, a.Ssid));
    void OnBattery(object? s, BatteryAutoListener.ChangedEventArgs a) => Raise(() => new BatteryPercentageAutomationEvent(a.Percentage));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started)
            {
                try
                {
                    _display!.Changed -= OnDisplay; _native!.Changed -= OnNative; _power!.Changed -= OnPower;
                    _mode!.Changed -= OnMode; _god!.PresetChanged -= OnPreset; _session!.Changed -= OnSession;
                }
                catch (Exception e) { Log.Error("unsubscribe LLT events", e); }
                await SetAutoAsync(Need.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) { Log.Error("dispose LLT events", e); }
        finally { _gate.Release(); }
    }
}
