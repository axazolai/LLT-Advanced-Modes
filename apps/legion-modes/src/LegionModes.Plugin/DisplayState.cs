using System.Runtime.InteropServices;
using System.Windows.Interop;
using LegionModes.Core.Engine;
using LenovoLegionToolkit.Lib.Station.Services;

namespace LegionModes.Plugin;

// The console display state (off / on / dimmed) from GUID_CONSOLE_DISPLAY_STATE, through a message-only window on the UI
// thread. LLT's own listener drops "dimmed". Changed is raised on the UI thread; handlers must not throw (they are guarded).
internal sealed class DisplayStateWatcher : IDisposable
{
    static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
    const int HWND_MESSAGE = -3;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);  // DEVICE_NOTIFY_WINDOW_HANDLE = 0

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    HwndSource? _source;
    IntPtr _notify;
    int _state = (int)DisplayStateKind.On;

    public DisplayStateKind State => (DisplayStateKind)Volatile.Read(ref _state);
    public bool Registered => _notify != IntPtr.Zero;
    public string? Error { get; private set; }
    public event Action<DisplayStateKind>? Changed;

    // Never throws: a failure leaves Registered false and Error set.
    public static async Task<DisplayStateWatcher> StartAsync(IUiDispatcher ui)
    {
        var watcher = new DisplayStateWatcher();
        try { await ui.InvokeAsync(watcher.Create).ConfigureAwait(false); }
        catch (Exception e)
        {
            Log.Error("display state registration", e);
            watcher.Error = e.Message;
        }
        return watcher;
    }

    void Create()
    {
        _source = new HwndSource(new HwndSourceParameters("LegionModes.DisplayState")
        {
            ParentWindow = new IntPtr(HWND_MESSAGE), Width = 0, Height = 0, WindowStyle = 0,
        });
        _source.AddHook(Hook);
        var guid = ConsoleDisplayState;
        _notify = RegisterPowerSettingNotification(_source.Handle, ref guid, 0);
        if (_notify == IntPtr.Zero) Error = $"RegisterPowerSettingNotification failed: {Marshal.GetLastWin32Error()}";
    }

    // lParam points to POWERBROADCAST_SETTING: Guid PowerSetting; uint DataLength; byte Data[]. Data[0]: 0 off, 1 on, 2 dimmed.
    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (msg != WM_POWERBROADCAST || wParam.ToInt64() != PBT_POWERSETTINGCHANGE || lParam == IntPtr.Zero) return IntPtr.Zero;
            if (Marshal.PtrToStructure<Guid>(lParam) != ConsoleDisplayState) return IntPtr.Zero;
            if (Marshal.ReadInt32(lParam, 16) < 1) return IntPtr.Zero;
            var value = Marshal.ReadByte(lParam, 20);
            if (value > (int)DisplayStateKind.Dimmed) return IntPtr.Zero;
            var next = (DisplayStateKind)value;
            if (Interlocked.Exchange(ref _state, (int)next) == (int)next) return IntPtr.Zero;
            Changed?.Invoke(next);
        }
        catch (Exception e) { Log.Error("display state message", e); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try { if (_notify != IntPtr.Zero) UnregisterPowerSettingNotification(_notify); } catch (Exception e) { Log.Error("unregister display state", e); }
        _notify = IntPtr.Zero;
        try { _source?.RemoveHook(Hook); _source?.Dispose(); } catch (Exception e) { Log.Error("dispose display state window", e); }
        _source = null;
    }
}
