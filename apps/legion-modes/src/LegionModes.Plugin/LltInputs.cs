using LegionModes.Core.Model;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.AutoListeners;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.System;

namespace LegionModes.Plugin;

// Turns LLT's listeners into the current values of Source, Lid and ExternalDisplay and into a few callbacks.
// Every handler catches and logs: an exception thrown here would propagate into LLT's raising code.
internal sealed class LltInputs : IAsyncDisposable
{
    NativeWindowsMessageListener? _native;
    PowerStateListener? _power;
    PowerModeListener? _mode;
    DisplayConfigurationListener? _display;
    InstanceStartedEventAutoAutoListener? _started;
    InstanceStoppedEventAutoAutoListener? _stopped;
    bool _subscribed;
    long _lastResumeTicks;   // Environment.TickCount64 of the last resume that was passed on; 0 = none

    // LLT's PowerStateListener raises a resume up to twice; two within this time count once.
    static readonly TimeSpan ResumeDedup = TimeSpan.FromSeconds(5);

    volatile int _source = (int)PowerSource.Ac;
    volatile int _lid = (int)LidState.Open;
    volatile bool _external;

    public PowerSource Source => (PowerSource)_source;
    public LidState Lid => (LidState)_lid;
    public bool ExternalDisplay => _external;

    // A value changed (not a resume, not a power mode change).
    public event Action? Changed;
    public event Action? Resumed;
    public event Action<PowerModeState>? PowerModeChanged;
    public event Action<int, string>? ProcessStarted;
    public event Action<int>? ProcessStopped;

    // A refresh asked for while one is running makes the running one read again, and the caller waits for it.
    sealed class Refresher
    {
        readonly object _g = new();
        Task? _run;
        bool _dirty;

        public Task RunAsync(Func<Task> read)
        {
            lock (_g)
            {
                if (_run is not null) { _dirty = true; return _run; }
                return _run = Loop(read);
            }
        }

        async Task Loop(Func<Task> read)
        {
            await Task.Yield();
            while (true)
            {
                lock (_g) _dirty = false;
                try { await read().ConfigureAwait(false); }
                catch (Exception e) { Log.Error("input refresh", e); }
                lock (_g)
                {
                    if (!_dirty) { _run = null; return; }
                }
            }
        }
    }

    readonly Refresher _sourceRefresh = new();
    readonly Refresher _externalRefresh = new();

    // Every subscription comes first, then the first read: a change in between is seen by the event or by the read.
    public async Task StartAsync()
    {
        _native = IoCContainer.Resolve<NativeWindowsMessageListener>();
        _power = IoCContainer.Resolve<PowerStateListener>();
        _mode = IoCContainer.Resolve<PowerModeListener>();
        _display = IoCContainer.Resolve<DisplayConfigurationListener>();
        _started = IoCContainer.Resolve<InstanceStartedEventAutoAutoListener>();
        _stopped = IoCContainer.Resolve<InstanceStoppedEventAutoAutoListener>();

        _native.Changed += OnNative;
        _power.Changed += OnPowerState;
        _mode.Changed += OnPowerMode;
        _display.Changed += OnDisplay;
        _subscribed = true;
        try { await _started.SubscribeChangedAsync(OnStarted).ConfigureAwait(false); }
        catch (Exception e) { Log.Error("process start subscription", e); }
        try { await _stopped.SubscribeChangedAsync(OnStopped).ConfigureAwait(false); }
        catch (Exception e) { Log.Error("process stop subscription", e); }

        try { await _native.EnsureInitializedAsync().ConfigureAwait(false); }
        catch (Exception e) { Log.Error("lid listener init", e); }
        await RefreshAsync().ConfigureAwait(false);
    }

    // Re-reads all three values from LLT (start, resume, the 30 s scan).
    public async Task RefreshAsync()
    {
        try { _lid = (int)(_native!.IsLidOpen ? LidState.Open : LidState.Closed); }
        catch (Exception e) { Log.Error("read lid", e); }
        await RefreshSourceAsync().ConfigureAwait(false);
        await RefreshExternalAsync().ConfigureAwait(false);
    }

    Task RefreshSourceAsync() => _sourceRefresh.RunAsync(async () =>
    {
        var s = await Power.IsPowerAdapterConnectedAsync().ConfigureAwait(false);
        _source = (int)(s switch
        {
            PowerAdapterStatus.Connected => PowerSource.Ac,
            PowerAdapterStatus.ConnectedLowWattage => PowerSource.Weak,
            _ => PowerSource.Battery,
        });
    });

    Task RefreshExternalAsync() => _externalRefresh.RunAsync(async () =>
    {
        _external = await Task.Run(Apply.DisplayPresence.ExternalConnected).ConfigureAwait(false);
    });

    static void Raise(Action? a, string what)
    {
        try { a?.Invoke(); } catch (Exception e) { Log.Error(what, e); }
    }

    async void OnNative(object? sender, NativeWindowsMessageListener.ChangedEventArgs e)
    {
        try
        {
            switch (e.Message)
            {
                case NativeWindowsMessage.LidOpened: _lid = (int)LidState.Open; Raise(Changed, "lid event"); break;
                case NativeWindowsMessage.LidClosed: _lid = (int)LidState.Closed; Raise(Changed, "lid event"); break;
                case NativeWindowsMessage.ExternalMonitorConnected:
                case NativeWindowsMessage.ExternalMonitorDisconnected:
                    await RefreshExternalAsync().ConfigureAwait(false);
                    Raise(Changed, "external display event");
                    break;
            }
        }
        catch (Exception ex) { Log.Error("native message handler", ex); }
    }

    async void OnDisplay(object? sender, DisplayConfigurationListener.ChangedEventArgs e)
    {
        try
        {
            var before = _external;
            await RefreshExternalAsync().ConfigureAwait(false);
            if (before != _external) Raise(Changed, "display change");
        }
        catch (Exception ex) { Log.Error("display change handler", ex); }
    }

    async void OnPowerState(object? sender, PowerStateListener.ChangedEventArgs e)
    {
        try
        {
            if (e.PowerStateEvent == PowerStateEvent.Resume)
            {
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastResumeTicks);
                if (last != 0 && now - last < ResumeDedup.TotalMilliseconds) return;
                Interlocked.Exchange(ref _lastResumeTicks, now);
                await RefreshAsync().ConfigureAwait(false);
                Raise(Resumed, "resume");
            }
            else if (e.PowerAdapterStateChanged)
            {
                await RefreshSourceAsync().ConfigureAwait(false);
                Raise(Changed, "power source event");
            }
        }
        catch (Exception ex) { Log.Error("power state handler", ex); }
    }

    void OnPowerMode(object? sender, PowerModeListener.ChangedEventArgs e)
    {
        try { PowerModeChanged?.Invoke(e.State); }
        catch (Exception ex) { Log.Error("power mode handler", ex); }
    }

    void OnStarted(object? sender, InstanceStartedEventAutoAutoListener.ChangedEventArgs e)
    {
        try { ProcessStarted?.Invoke(e.ProcessId, e.ProcessName); }
        catch (Exception ex) { Log.Error("process start handler", ex); }
    }

    void OnStopped(object? sender, InstanceStoppedEventAutoAutoListener.ChangedEventArgs e)
    {
        try { ProcessStopped?.Invoke(e.ProcessId); }
        catch (Exception ex) { Log.Error("process stop handler", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_subscribed) return;
        _subscribed = false;
        try { _native!.Changed -= OnNative; _power!.Changed -= OnPowerState; _mode!.Changed -= OnPowerMode; _display!.Changed -= OnDisplay; }
        catch (Exception e) { Log.Error("unsubscribe", e); }
        try { await _started!.UnsubscribeChangedAsync(OnStarted).ConfigureAwait(false); } catch (Exception e) { Log.Error("unsubscribe start", e); }
        try { await _stopped!.UnsubscribeChangedAsync(OnStopped).ConfigureAwait(false); } catch (Exception e) { Log.Error("unsubscribe stop", e); }
    }
}
