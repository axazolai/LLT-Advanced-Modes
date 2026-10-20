using LegionModes.Core;
using Application = System.Windows.Application;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LenovoLegionToolkit.Lib.Station.Services;

namespace LegionModes.Plugin.Shell;

// Places the tray block and the dashboard card into LLT's main window. LLT builds its tray helper in the window's Loaded
// handler (after awaits), so the placement is tried at once, on the window's Loaded and every 2 s for 60 s. A member that
// is still missing after that sets Status.ShellError; the page keeps working.
internal sealed class ShellInjector : IDisposable
{
    static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    readonly Runtime _runtime;
    readonly IUiDispatcher _ui;
    readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly string[] _misses = new string[2];
    DispatcherTimer? _timer;
    DateTime _started;
    TrayBlock? _tray;
    DashboardCard? _card;
    bool _hooked;
    bool _disposed;
    bool _finished;
    Window? _window;

    public ShellInjector(Runtime runtime, IUiDispatcher ui)
    {
        _runtime = runtime;
        _ui = ui;
    }

    // Completes when both parts are placed or the time is up (ShellError then says what is missing).
    public Task Completed => _done.Task;

    public void Start()
    {
        try { _ = _ui.InvokeAsync(StartOnUi); }
        catch (Exception e) { Log.Error("shell start", e); _done.TrySetResult(); }
    }

    void StartOnUi()
    {
        try
        {
            _started = DateTime.UtcNow;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RetryPeriod };
            _timer.Tick += (_, _) => Attempt();
            _timer.Start();
            Attempt();
        }
        catch (Exception e)
        {
            Log.Error("shell start", e);
            _done.TrySetResult();
        }
    }

    void Attempt()
    {
        try
        {
            if (_disposed) return;
            var window = Application.Current?.MainWindow;
            if (window is not null && _window is not null && !ReferenceEquals(window, _window)) Replaced(window);
            if (_finished) return;
            _window ??= window;
            if (window is not null && !_hooked && !window.IsLoaded)
            {
                _hooked = true;
                window.Loaded += (_, _) => Attempt();
            }

            _misses[0] = _tray is null ? TryTray(window) : "";
            _misses[1] = _card is null ? TryCard(window) : "";

            if (_misses.All(m => m.Length == 0))
            {
                Finish(null);
                return;
            }
            if (DateTime.UtcNow - _started > Limit) Finish(string.Join("; ", _misses.Where(m => m.Length > 0)));
        }
        catch (Exception e) { Log.Error("shell injection attempt", e); }
    }

    // LLT re-created its main window (a language or appearance change): the old tray menu and dashboard are gone, so the
    // parts are placed again into the new window.
    void Replaced(Window window)
    {
        try { Tr.FromLlt(); _tray?.Dispose(); _card?.Dispose(); }
        catch (Exception e) { Log.Error("shell replace", e); }
        _tray = null;
        _card = null;
        _window = window;
        _hooked = false;
        _finished = false;
        _started = DateTime.UtcNow;
    }

    // Each part keeps its own result; ShellError is the join of all of them, so one part succeeding never clears another's error.
    string? _timeoutError, _cardError;

    void Publish()
    {
        var all = new[] { _timeoutError, _cardError }.Where(e => !string.IsNullOrEmpty(e)).ToArray();
        _runtime.Status.ShellError = all.Length == 0 ? null : string.Join("; ", all);
    }

    void OnCardReport(string? error)
    {
        if (error == _cardError) return;
        _cardError = error;
        if (error is not null) Log.Error("shell injection", new InvalidOperationException(error));
        Publish();
    }

    void Finish(string? timeoutError)
    {
        _finished = true;   // the timer keeps running: it notices a re-created window
        _timeoutError = timeoutError;
        if (timeoutError is not null) Log.Error("shell injection", new InvalidOperationException(timeoutError));
        Publish();
        _done.TrySetResult();
    }

    // "" = placed; otherwise what is missing.
    string TryTray(Window? window)
    {
        try
        {
            if (window is null) return "tray: main window not found";
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var trayHelper = window.GetType().GetField("_trayHelper", Private)?.GetValue(window);
            var menu = trayHelper?.GetType().GetField("_contextMenu", Private)?.GetValue(trayHelper) as ContextMenu;
            if (menu is null) return "tray: _trayHelper/_contextMenu not found";
            var tray = new TrayBlock(_runtime, menu);
            try { tray.Insert(); }
            catch { tray.Dispose(); throw; }
            _tray = tray;
            return "";
        }
        catch (Exception e)
        {
            Log.Error("tray injection", e);
            return "tray: " + e.Message;
        }
    }

    string TryCard(Window? window)
    {
        try
        {
            if (window is null) return "dashboard: main window not found";
            if (window.FindName("_rootFrame") is not Frame frame) return "dashboard: _rootFrame not found";
            _card = new DashboardCard(_runtime, frame, OnCardReport);   // reports its own outcome; a later visit can still fail
            return "";
        }
        catch (Exception e)
        {
            Log.Error("dashboard injection", e);
            return "dashboard: " + e.Message;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _ = _ui.InvokeAsync(() =>
            {
                try { _timer?.Stop(); _tray?.Dispose(); _card?.Dispose(); }
                catch (Exception e) { Log.Error("shell dispose", e); }
            });
        }
        catch (Exception e) { Log.Error("shell dispose", e); }
        _done.TrySetResult();
    }
}
