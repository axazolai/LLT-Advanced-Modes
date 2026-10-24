using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LegionModes.Core.Sensors;

namespace LegionModes.Tray;

// Owns the two tray icons through Shell_NotifyIcon with a fixed GUID each (so Windows keeps the pin setting of each icon across the
// dGPU icon's disappearances, keeper restarts and updates), on a thread of its own with a hidden top-level window. The shell is asked to
// add the icons again when Explorer restarts (TaskbarCreated) and on every update while an add has failed (Explorer not up yet).
// Nothing is written to the registry: pinning is the owner's Windows setting.
public sealed class TrayIconHost : ITrayView, IDisposable
{
    static readonly Guid CpuGuid = new("654d0b40-cbda-4916-b428-656a33686c1c");
    static readonly Guid DgpuGuid = new("022fd1be-6062-4a4d-a991-90b3877789ca");

    const uint WmDestroy = 0x0002, WmClose = 0x0010, WmApp = 0x8000, WmCallback = 0x8001;
    const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifGuid = 0x20, NifShowTip = 0x80;
    const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    const int ErrorClassExists = 1410;
    const uint MsgFltAllow = 1;

    readonly ConcurrentQueue<(Sensor Sensor, IconState? State)> _queue = new();
    readonly Dictionary<Sensor, IconState> _shown = new();   // host thread only
    readonly HashSet<Sensor> _added = new();                 // host thread only
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly WndProc _proc;                                   // kept alive for the window class
    IntPtr _hwnd;
    uint _taskbarCreated;
    Exception? _startError;

    public TrayIconHost()
    {
        _proc = WindowProc;
        _thread = new Thread(Loop) { IsBackground = true, Name = "TrayIconHost" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startError is not null) throw new InvalidOperationException("tray window not created: " + _startError.Message, _startError);
    }

    public void Show(Sensor sensor, IconState state) => Post(sensor, state);

    public void Hide(Sensor sensor) => Post(sensor, null);

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    void Post(Sensor sensor, IconState? state)
    {
        _queue.Enqueue((sensor, state));
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmApp, IntPtr.Zero, IntPtr.Zero);
    }

    void Loop()
    {
        try
        {
            var instance = GetModuleHandle(null);
            var cls = new WndClass { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = instance, lpszClassName = "LegionModes.TrayHost" };
            if (RegisterClass(ref cls) == 0 && Marshal.GetLastWin32Error() != ErrorClassExists)
                throw new InvalidOperationException("RegisterClass failed: " + Marshal.GetLastWin32Error());
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _hwnd = CreateWindowEx(0, cls.lpszClassName, "LegionModes tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            // The keeper runs elevated; the shell does not, so UIPI would drop TaskbarCreated. A false result is ignored: every Show retries a failed add.
            ChangeWindowMessageFilterEx(_hwnd, _taskbarCreated, MsgFltAllow, IntPtr.Zero);
        }
        catch (Exception e)
        {
            _startError = e;
            _ready.Set();
            return;
        }
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WmApp) { Drain(); return IntPtr.Zero; }
            if (_taskbarCreated != 0 && msg == _taskbarCreated)
            {
                _added.Clear();
                foreach (var (sensor, state) in _shown.ToList()) Apply(sensor, state);
                return IntPtr.Zero;
            }
            if (msg == WmClose)
            {
                foreach (var sensor in _added.ToList()) Remove(sensor);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            if (msg == WmDestroy) { PostQuitMessage(0); return IntPtr.Zero; }
        }
        catch { /* a window procedure must not throw into the message loop */ }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    void Drain()
    {
        while (_queue.TryDequeue(out var item))
        {
            if (item.State is null) Remove(item.Sensor);
            else Apply(item.Sensor, item.State);
        }
    }

    void Apply(Sensor sensor, IconState state)
    {
        _shown[sensor] = state;
        using var bitmap = IconRenderer.Render(sensor, state.Digits, state.Zone);
        var icon = bitmap.GetHicon();
        try
        {
            var data = Data(sensor, icon, state.Tooltip);
            var wasAdded = _added.Contains(sensor);
            var ok = Shell_NotifyIcon(wasAdded ? NimModify : NimAdd, ref data);
            if (!ok && wasAdded) ok = Shell_NotifyIcon(NimAdd, ref data);   // the shell lost the icon
            if (!ok && !wasAdded)
            {
                // A stale entry of the same GUID (a keeper that died): delete it and add afresh.
                var stale = Data(sensor, IntPtr.Zero, "");
                stale.uFlags = NifGuid;
                Shell_NotifyIcon(NimDelete, ref stale);
                ok = Shell_NotifyIcon(NimAdd, ref data);
            }
            if (ok) _added.Add(sensor);
            else _added.Remove(sensor);   // the shell is not ready: the next update tries again
        }
        finally { DestroyIcon(icon); }   // the shell keeps its own copy
    }

    void Remove(Sensor sensor)
    {
        _shown.Remove(sensor);
        if (!_added.Remove(sensor)) return;
        var data = Data(sensor, IntPtr.Zero, "");
        data.uFlags = NifGuid;
        Shell_NotifyIcon(NimDelete, ref data);
    }

    NotifyIconData Data(Sensor sensor, IntPtr icon, string tooltip) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uFlags = NifMessage | NifIcon | NifTip | NifGuid | NifShowTip,
        uCallbackMessage = WmCallback,
        hIcon = icon,
        szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
        guidItem = sensor == Sensor.Cpu ? CpuGuid : DgpuGuid,
    };

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClassW")]
    static extern ushort RegisterClass(ref WndClass cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilterStruct);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    static extern IntPtr GetModuleHandle(string? name);
}
