using LegionModes.Core;
using Timer = System.Threading.Timer;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LegionModes.Core.Status;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Listeners;

namespace LegionModes.Plugin.Status;

// The camera's privacy switch as the last LLT key event set it (the device stays present either way).
public sealed record CameraSwitch(bool Off, DateTimeOffset At);

// Microphone mute and use, camera presence, privacy switch and use. Values change on any thread; PropertyChanged is posted through `post`.
public sealed class DeviceStatus : INotifyPropertyChanged, IDisposable
{
    static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    readonly Action<Action> _post;
    readonly object _gate = new();
    CameraDevice? _watcher;
    SpecialKeyListener? _keys;
    Timer? _poll, _cameraTimer;
    int _reading;
    int _again;
    int _cameraAgain;
    int _polls;
    int _disposed;

    public DeviceStatus(Action<Action>? post = null) => _post = post ?? (a => a());

    public event PropertyChangedEventHandler? PropertyChanged;

    bool? _micMuted;
    string[] _micInUseBy = [];
    CameraState _camera = CameraState.Unknown;
    string[] _cameraInUseBy = [];
    CameraSwitch? _switch;

    public bool? MicMuted { get => _micMuted; private set => Set(ref _micMuted, value); }
    public string[] MicInUseBy { get => _micInUseBy; private set => SetList(ref _micInUseBy, value); }
    public CameraState Camera { get => _camera; private set => Set(ref _camera, value); }
    public string[] CameraInUseBy { get => _cameraInUseBy; private set => SetList(ref _cameraInUseBy, value); }
    public CameraSwitch? Switch { get => _switch; private set => Set(ref _switch, value); }

    // Raised on the listener's thread when the privacy switch moves; the runtime keeps it in control.json.
    public event Action<CameraSwitch>? CameraSwitched;

    // The last switch kept in control.json, read at start (before any key event).
    public void RestoreSwitch(CameraSwitch? last) { if (_switch is null) Switch = last; }

    public string MicrophoneWord => MicMuted switch { true => Tr.T("выключен"), false => Tr.T("включён"), _ => Tr.T("нет данных") };
    public string CameraPresence => Camera switch { CameraState.Present => Tr.T("подключена"), CameraState.Absent => Tr.T("не найдена"), _ => Tr.T("нет данных") };

    // «выключена (по последнему переключению, 14:32)», «неизвестно» before the first key event.
    public string SwitchText => Switch is { } s ? Tr.F("{0} (по последнему переключению, {1})", s.Off ? Tr.T("выключена") : Tr.T("включена"), When(s.At)) : Tr.T("неизвестно");

    public string MicrophoneText => Tr.F("Микрофон: {0}{1}", MicrophoneWord, Users(MicInUseBy));

    public string CameraText => Tr.F("Камера: {0}{1}", Camera == CameraState.Absent || Switch is null ? CameraPresence : Switch.Off ? Tr.T("выключена") : Tr.T("включена"), Users(CameraInUseBy));

    // The tray's one line: «Микрофон включён · камера выключена (14:32)».
    public string ShortText
    {
        get
        {
            var camera = Camera == CameraState.Absent ? Tr.T("не найдена")
                : Switch is { } s ? $"{(s.Off ? Tr.T("выключена") : Tr.T("включена"))} ({When(s.At)})"
                : CameraPresence;
            return Tr.F("Микрофон {0} · камера {1}", MicrophoneWord, camera);
        }
    }

    static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("dd.MM HH:mm");
    }

    static string Users(string[] apps) => apps.Length == 0 ? "" : Tr.F("; используют: {0}", UsersList(apps));

    public static string UsersList(string[] apps) => string.Join(", ", apps.Select(Friendly));

    // "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "Microsoft.WindowsCamera"
    static string Friendly(string app)
    {
        var i = app.IndexOf('_');
        return i > 0 && !app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..i] : app;
    }

    // Runs the first read, then keeps the values current. Safe to call once.
    public void Start()
    {
        try
        {
            _watcher = new CameraDevice(OnCameraNotification);
            if (!_watcher.Watch())
                Log.Error("camera notifications", new InvalidOperationException("no device-interface notification could be registered; the camera is re-read every 30 s"));
        }
        catch (Exception e) { Log.Error("camera watcher", e); }
        try
        {
            _keys = IoCContainer.Resolve<SpecialKeyListener>();
            _keys.Changed += OnSpecialKey;
        }
        catch (Exception e) { Log.Error("special key listener", e); _keys = null; }
        _cameraTimer = new Timer(_ => _ = RefreshAsync(cameraToo: true), null, Timeout.Infinite, Timeout.Infinite);
        _poll = new Timer(_ => _ = RefreshAsync(cameraToo: Interlocked.Increment(ref _polls) % 6 == 0), null, PollPeriod, PollPeriod);   // camera every 30 s
        _ = RefreshAsync(cameraToo: true);
    }

    void OnSpecialKey(object? sender, SpecialKeyListener.ChangedEventArgs e)
    {
        try
        {
            if (e.SpecialKey is not (SpecialKey.CameraOn or SpecialKey.CameraOff)) return;
            var now = new CameraSwitch(e.SpecialKey == SpecialKey.CameraOff, DateTimeOffset.Now);
            Switch = now;
            try { CameraSwitched?.Invoke(now); }
            catch (Exception ex) { Log.Error("camera switch handler", ex); }
            OnCameraNotification();
        }
        catch (Exception ex) { Log.Error("special key handler", ex); }
    }

    // A burst of device events becomes one read.
    void OnCameraNotification()
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0) _cameraTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
        catch (Exception e) { Log.Error("camera notification", e); }
    }

    public async Task RefreshAsync(bool cameraToo = true)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        if (cameraToo) Volatile.Write(ref _cameraAgain, 1);
        // A request that arrives during a read is not dropped: the read runs once more.
        if (Interlocked.Exchange(ref _reading, 1) == 1) { Volatile.Write(ref _again, 1); return; }
        try
        {
            do
            {
                Volatile.Write(ref _again, 0);
                await ReadOnceAsync(Interlocked.Exchange(ref _cameraAgain, 0) == 1).ConfigureAwait(false);
            }
            while (Volatile.Read(ref _again) == 1 && Volatile.Read(ref _disposed) == 0);
        }
        catch (Exception e) { Log.Error("device status refresh", e); }
        finally { Volatile.Write(ref _reading, 0); }
        if (Volatile.Read(ref _again) == 1 && Volatile.Read(ref _disposed) == 0) _ = RefreshAsync(false);
    }

    async Task ReadOnceAsync(bool cameraToo)
    {
        {
            try
            {
                var mic = IoCContainer.Resolve<MicrophoneFeature>();
                MicMuted = await mic.IsSupportedAsync().ConfigureAwait(false)
                    ? await mic.GetStateAsync().ConfigureAwait(false) == MicrophoneState.Off
                    : null;
            }
            catch (Exception e) { Log.Error("read microphone", e); MicMuted = null; }
            try { MicInUseBy = ConsentStore.InUse(ConsentStore.Microphone).ToArray(); }
            catch (Exception e) { Log.Error("read microphone use", e); }
            try { CameraInUseBy = ConsentStore.InUse(ConsentStore.Webcam).ToArray(); }
            catch (Exception e) { Log.Error("read camera use", e); }
            if (cameraToo) Camera = await Task.Run(CameraDevice.Read).ConfigureAwait(false);
        }
    }

    void SetList(ref string[] field, string[] value, [CallerMemberName] string? name = null)
    {
        if (field.SequenceEqual(value)) return;
        Set(ref field, value, name);
    }

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
        }
        var handler = PropertyChanged;
        if (handler is null) return;
        _post(() =>
        {
            try { handler(this, new PropertyChangedEventArgs(name)); }
            catch (Exception e) { Log.Error("device status handler", e); }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { if (_keys is not null) _keys.Changed -= OnSpecialKey; } catch (Exception e) { Log.Error("unsubscribe special keys", e); }
        try { _poll?.Dispose(); _cameraTimer?.Dispose(); } catch (Exception e) { Log.Error("dispose device timers", e); }
        try { _watcher?.Dispose(); } catch (Exception e) { Log.Error("dispose camera watcher", e); }
    }
}
