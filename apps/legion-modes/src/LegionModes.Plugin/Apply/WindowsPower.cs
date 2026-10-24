using LegionModes.Core;
using System.Runtime.InteropServices;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LenovoLegionToolkit.Lib.Controllers;

namespace LegionModes.Plugin.Apply;

// Idle timers of one power scheme, AC and DC, and the scheme primitives the processor applier shares. Timer values come from WindowsTimers (seconds).
internal static class WindowsPower
{
    static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    static readonly Guid VideoDim = new("17aaa29b-8b43-4b94-aafe-35f64daaf1ee");
    static readonly Guid VideoDimmedBrightness = new("f1fbfde2-a960-4165-9f88-50667911ce96");
    static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    static readonly Guid HibernateIdle = new("9d7815a6-7ee4-497e-8888-515a05f02364");

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("powrprof.dll")]
    static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    static readonly Guid OverlayBestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    // The four stage timers of the active scheme on the present power source, read live from the system.
    internal static (LegionModes.Core.Engine.WindowsTimerValues Values, bool OnBattery) ReadTimers()
    {
        var scheme = ActiveScheme();
        var onBattery = GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
        uint Read(Guid sub, Guid setting)
        {
            var rc = onBattery ? PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var v) : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
            if (rc != 0) throw new ApplyResultException($"read timer failed: {rc}");
            return v;
        }
        return (new LegionModes.Core.Engine.WindowsTimerValues(Read(SubVideo, VideoDim), Read(SubVideo, VideoDimmedBrightness), Read(SubVideo, VideoIdle),
            Read(SubSleep, StandbyIdle), Read(SubSleep, HibernateIdle), null), onBattery);
    }

    // What Windows holds now for the processor: the boost and EPP indexes of the active scheme on the present power source, and the power
    // mode overlay (null when it is none of the three the plugin sets). Read live from the system.
    internal static (uint? Boost, uint? Epp, Core.Model.WindowsPowerMode? Overlay, bool OnBattery) ReadProcessor()
    {
        var scheme = ActiveScheme();
        var sub = ProcessorApplier.SubProcessor;
        var boostId = ProcessorApplier.ProcessorBoost;
        var eppId = ProcessorApplier.ProcessorEpp;
        var onBattery = GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
        uint? Read(Guid setting) =>
            (onBattery ? PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var v) : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v)) == 0
                ? v : null;
        Core.Model.WindowsPowerMode? overlay = null;
        if (PowerGetEffectiveOverlayScheme(out var o) == 0)
            overlay = o == Guid.Empty ? Core.Model.WindowsPowerMode.Balanced
                : o == OverlayBestPowerEfficiency ? Core.Model.WindowsPowerMode.BestPowerEfficiency
                : o == OverlayBestPerformance ? Core.Model.WindowsPowerMode.BestPerformance : null;
        return (Read(boostId), Read(eppId), overlay, onBattery);
    }

    internal static Guid ActiveScheme()
    {
        var rc = PowerGetActiveScheme(IntPtr.Zero, out var p);
        if (rc != 0) throw new ApplyResultException($"PowerGetActiveScheme failed: {rc}");
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    // scheme: the one stored in the control record; null = the active scheme. The scheme is re-activated only when it is
    // the active one (that makes the new values live).
    public static void ApplyTimers(Guid? scheme, WindowsTimerValues v)
    {
        var target = scheme ?? ActiveScheme();
        Write(target, SubVideo, VideoIdle, v.VideoIdleSec, "display off");
        Write(target, SubVideo, VideoDim, v.DimSec, "dim");
        Write(target, SubVideo, VideoDimmedBrightness, v.DimmedBrightness, "dimmed brightness");
        Write(target, SubSleep, StandbyIdle, v.StandbyIdleSec, "sleep");
        Write(target, SubSleep, HibernateIdle, v.HibernateIdleSec, "hibernate");
        Activate(target);
    }

    // The scheme is re-activated only when it is the active one (that makes the new values live).
    internal static void Activate(Guid target)
    {
        if (ActiveScheme() != target) return;
        var rc = PowerSetActiveScheme(IntPtr.Zero, ref target);
        if (rc != 0) throw new ApplyResultException($"PowerSetActiveScheme failed: {rc}");
    }

    // Writes AC and DC, then reads both back: a value the system did not keep is an error.
    internal static void Write(Guid scheme, Guid subGroup, Guid setting, uint value, string what)
    {
        var rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, value);
        if (rc != 0) throw new ApplyResultException($"write AC {what} failed: {rc}");
        rc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, value);
        if (rc != 0) throw new ApplyResultException($"write DC {what} failed: {rc}");
        rc = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out var ac);
        if (rc != 0) throw new ApplyResultException($"read AC {what} failed: {rc}");
        rc = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out var dc);
        if (rc != 0) throw new ApplyResultException($"read DC {what} failed: {rc}");
        if (ac != value || dc != value)
            throw new ApplyResultException(Tr.F("{0}: written {1}, read back AC {2} / DC {3}", what, value, ac, dc));
    }
}
