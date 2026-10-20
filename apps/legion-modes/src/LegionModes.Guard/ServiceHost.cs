using System.ComponentModel;
using System.Runtime.InteropServices;
using LegionModes.Core;

namespace LegionModes.Guard;

internal static unsafe class ServiceHost
{
    const int SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_START_PENDING = 2, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4, SERVICE_STOPPED = 1;
    const int ERROR_SERVICE_SPECIFIC_ERROR = 1066;
    const int SERVICE_ACCEPT_STOP = 1, SERVICE_ACCEPT_POWEREVENT = 0x40, SERVICE_ACCEPT_SESSIONCHANGE = 0x80;
    const int SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_POWEREVENT = 0x0D, SERVICE_CONTROL_SESSIONCHANGE = 0x0E;
    internal const int PBT_POWERSETTINGCHANGE = 0x8013, PBT_APMRESUMEAUTOMATIC = 0x12;
    const int DEVICE_NOTIFY_SERVICE_HANDLE = 1;
    internal static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
    static IntPtr _status; static Guard _guard = null!;
    static readonly Native.HandlerEx Handler = OnControl;           // kept alive for the process lifetime
    static readonly Native.ServiceMainProc MainProc = ServiceMain;

    // Returns when the service has stopped. Throws Win32Exception when the process is not under the SCM.
    public static void Run(Guard guard)
    {
        _guard = guard;
        var table = new[]
        {
            new Native.SERVICE_TABLE_ENTRY { Name = Marshal.StringToHGlobalUni(Paths.GuardService), Proc = Marshal.GetFunctionPointerForDelegate(MainProc) },
            default,
        };
        if (!Native.StartServiceCtrlDispatcher(table)) throw new Win32Exception();
    }

    static void ServiceMain(int argc, IntPtr argv)
    {
        _status = Native.RegisterServiceCtrlHandlerEx(Paths.GuardService, Handler, IntPtr.Zero);
        if (_status == 0) return;
        SetState(SERVICE_START_PENDING, 0);
        var lid = GUID_LIDSWITCH_STATE_CHANGE;
        if (Native.RegisterPowerSettingNotification(_status, ref lid, DEVICE_NOTIFY_SERVICE_HANDLE) == 0)
            GuardLog.Write("lid notification not registered: " + new Win32Exception().Message);
        GuardActions.EnableShutdownPrivilege();
        SetState(SERVICE_RUNNING, SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_POWEREVENT | SERVICE_ACCEPT_SESSIONCHANGE);
        try { _guard.Run(); }                      // blocks until Stop() is called; 5 s decision loop + pipe server
        catch (Exception e)
        {
            // A failure exit code lets the SCM recovery actions (failureflag) restart the service.
            GuardLog.Write("guard crashed: " + e);
            SetState(SERVICE_STOPPED, 0, win32Exit: ERROR_SERVICE_SPECIFIC_ERROR, specificExit: 1);
            return;
        }
        SetState(SERVICE_STOPPED, 0);
    }

    static int OnControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        try
        {
            switch (control)
            {
                case SERVICE_CONTROL_STOP: SetState(SERVICE_STOP_PENDING, 0); _guard.Stop(); return 0;
                case SERVICE_CONTROL_SESSIONCHANGE: _guard.SessionChanged(eventType); return 0;
                case SERVICE_CONTROL_POWEREVENT when eventType == PBT_APMRESUMEAUTOMATIC: _guard.Resumed(); return 0;
                case SERVICE_CONTROL_POWEREVENT when eventType == PBT_POWERSETTINGCHANGE:
                    var s = (Native.POWERBROADCAST_SETTING*)eventData;
                    if (s->PowerSetting == GUID_LIDSWITCH_STATE_CHANGE) _guard.Lid(open: s->Data != 0);
                    return 0;
                default: return 0;
            }
        }
        catch (Exception e) { GuardLog.Write("control handler failed: " + e.Message); return 0; }
    }

    static void SetState(int state, int accept, int win32Exit = 0, int specificExit = 0)
    {
        var pending = state is SERVICE_START_PENDING or SERVICE_STOP_PENDING;
        var st = new Native.SERVICE_STATUS
        {
            ServiceType = SERVICE_WIN32_OWN_PROCESS, CurrentState = state, ControlsAccepted = accept,
            Win32ExitCode = win32Exit, ServiceSpecificExitCode = specificExit,
            CheckPoint = pending ? 1 : 0, WaitHint = pending ? 10000 : 0,
        };
        Native.SetServiceStatus(_status, ref st);
    }
}

// Console mode: the lid notification arrives through a message-only window instead of the service handle.
internal static unsafe class ConsoleLidWindow
{
    const uint WM_POWERBROADCAST = 0x218, WM_CLOSE = 0x10, WM_DESTROY = 0x2, WM_QUIT = 0x12;
    static readonly IntPtr HWND_MESSAGE = new(-3);
    static readonly Native.WndProc Proc = WndProc;
    static Guard _guard = null!;
    static IntPtr _hwnd;

    public static Thread Start(Guard guard)
    {
        _guard = guard;
        var t = new Thread(Loop) { IsBackground = true, Name = "lid-window" };
        t.Start();
        return t;
    }

    public static void Close() { if (_hwnd != 0) Native.PostMessage(_hwnd, WM_CLOSE, 0, 0); }

    static void Loop()
    {
        var name = Marshal.StringToHGlobalUni("LegionModesGuardLid");
        var wc = new Native.WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc), hInstance = Native.GetModuleHandle(null), lpszClassName = name };
        if (Native.RegisterClass(ref wc) == 0) { GuardLog.Write("lid window class failed: " + new Win32Exception().Message); return; }
        _hwnd = Native.CreateWindowEx(0, "LegionModesGuardLid", null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, wc.hInstance, 0);
        if (_hwnd == 0) { GuardLog.Write("lid window failed: " + new Win32Exception().Message); return; }
        var lid = ServiceHost.GUID_LIDSWITCH_STATE_CHANGE;
        if (Native.RegisterPowerSettingNotification(_hwnd, ref lid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */) == 0)
            GuardLog.Write("lid notification not registered: " + new Win32Exception().Message);
        while (Native.GetMessage(out var msg, 0, 0, 0) > 0) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
    }

    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_POWERBROADCAST)
            {
                var kind = (int)wParam;
                if (kind == ServiceHost.PBT_APMRESUMEAUTOMATIC) _guard.Resumed();
                else if (kind == ServiceHost.PBT_POWERSETTINGCHANGE)
                {
                    var s = (Native.POWERBROADCAST_SETTING*)lParam;
                    if (s->PowerSetting == ServiceHost.GUID_LIDSWITCH_STATE_CHANGE) _guard.Lid(open: s->Data != 0);
                }
                return 1;
            }
            if (msg == WM_CLOSE) { Native.DestroyWindow(hwnd); return 0; }
            if (msg == WM_DESTROY) { Native.PostMessage(0, WM_QUIT, 0, 0); return 0; }
        }
        catch (Exception e) { GuardLog.Write("lid window handler failed: " + e.Message); }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
