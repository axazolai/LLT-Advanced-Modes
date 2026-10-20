using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LegionModes.Core;
using LegionModes.Core.Programs;
using LegionModes.Core.Status;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Automation;
using LenovoLegionToolkit.Lib.Settings;

namespace LegionModes.Plugin;

public sealed partial class Runtime
{
    readonly SemaphoreSlim _controlGate = new(1, 1);
    readonly SemaphoreSlim _wakeGate = new(1, 1);
    readonly object _dataGate = new();
    ControlData _data = ControlData.Empty;          // control.json in memory; every change is saved through UpdateData

    // The one writer of control.json (control record, autonomy, clean-exit marker, abnormal-shutdown counter, wake overrides).
    void UpdateData(Func<ControlData, ControlData> change)
    {
        lock (_dataGate)
        {
            _data = change(_data);
            if (_controlUnreadable && !RecoverControlFile()) return;   // an unreadable record is never written over
            try { ControlFile.Save(ControlPath, _data); }
            catch (Exception e) { Log.Error("save control.json", e); }
        }
    }

    volatile bool _controlUnreadable;

    // _dataGate held. True when the file reads again (or is gone); the record on disk is then adopted for what it holds.
    bool RecoverControlFile()
    {
        var read = ControlFile.Read(ControlPath);
        if (read.Error is not null) return false;
        _controlUnreadable = false;
        Status.ControlError = null;
        if (!read.Missing) _data = KeepLive(read.Data);
        return true;
    }

    // A record adopted from disk keeps what only memory knows now: autonomy, the run markers and the camera switch.
    ControlData KeepLive(ControlData disk) => disk with
    {
        Autonomy = _data.Autonomy, Running = _data.Running, AbnormalShutdowns = _data.AbnormalShutdowns,
        CameraOff = _data.CameraOff ?? disk.CameraOff, CameraAt = _data.CameraAt ?? disk.CameraAt,
    };

    // Taking or returning control from the Control tab is the owner's word: the record is written from here on.
    void AcceptControlWrites()
    {
        lock (_dataGate)
        {
            _controlUnreadable = false;
            Status.ControlError = null;
        }
    }

    // Memory or the control.json record says the plugin holds control (Return works from the record after a failed restore).
    public bool ControlHeld => _hasControl || ControlFile.Load(ControlPath).HasControl;

    // The record that holds the originals: the one in memory, else the one on disk (adopted).
    ControlData Held()
    {
        lock (_dataGate)
        {
            if (_data.HasControl) return _data;
            var disk = ControlFile.Load(ControlPath);
            if (disk.HasControl) _data = KeepLive(disk);
            return _data;
        }
    }

    // Take control: back up automation.json, save the originals to control.json, then disable LLT automation, stop LLT's
    // power-mode mapping and set the lid action to "do nothing"; then the wake-request overrides of programs.json. A record
    // that already says HasControl is never overwritten (it holds the originals): Take re-asserts instead.
    public async Task<string?> TakeControlAsync()
    {
        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            AcceptControlWrites();
            var held = Held();
            if (held.HasControl)
            {
                await AssertControlAsync(held).ConfigureAwait(false);
            }
            else
            {
                BackupAutomation();
                var automation = IoCContainer.Resolve<AutomationProcessor>();
                var settings = IoCContainer.Resolve<ApplicationSettings>();
                var scheme = LidAction.ActiveScheme();
                var (ac, dc) = LidAction.Read(scheme);
                UpdateData(d => d with
                {
                    HasControl = true, SavedLidActionAc = ac, SavedLidActionDc = dc, SavedAutomationEnabled = automation.IsEnabled,
                    SavedPowerModeMappingMode = settings.Store.PowerModeMappingMode.ToString(), Scheme = scheme,
                });
                var saved = _data;
                try
                {
                    await AssertControlAsync(saved).ConfigureAwait(false);
                }
                catch
                {
                    // The record stays (HasControl: true) until a restore succeeds.
                    await TryRestoreAndClearAsync().ConfigureAwait(false);
                    throw;
                }
            }
            await SyncPresetsAsync().ConfigureAwait(false);   // the presets exist and match modes.json before the first apply
            lock (_gate) { _lastApplied = null; _retry.Clear(); _fullPending = true; _immediate = true; }
            SetHasControl(true);
            RescheduleSleep();
            await SyncWakeOverridesAsync().ConfigureAwait(false);
            Log.Trace("control taken");
            return null;
        }
        catch (Exception e)
        {
            Log.Error("take control", e);
            Status.LastApplyError = "take control: " + e.Message;
            return "take control: " + e.Message;
        }
        finally { _controlGate.Release(); }
    }

    // Works from control.json when memory says no control but the file says the plugin holds it.
    public async Task<string?> ReturnControlAsync()
    {
        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            AcceptControlWrites();
            var saved = Held();
            if (!saved.HasControl)
            {
                if (_hasControl) { SetHasControl(false); RescheduleSleep(); }
                return null;
            }
            // The engine stops first so nothing is applied while the saved values go back.
            SetHasControl(false);
            RescheduleSleep();
            await _applyGate.WaitAsync().ConfigureAwait(false);
            _applyGate.Release();
            lock (_gate) { _lastApplied = null; _retry.Clear(); }
            await RemoveWakeOverridesAsync().ConfigureAwait(false);
            if (await TryRestoreAndClearAsync().ConfigureAwait(false)) { Log.Trace("control returned"); return null; }
            const string failed = "return control: restore failed, the saved values are kept in control.json";
            Status.LastApplyError = failed;
            return failed;
        }
        catch (Exception e)
        {
            Log.Error("return control", e);
            Status.LastApplyError = "return control: " + e.Message;
            return "return control: " + e.Message;
        }
        finally { _controlGate.Release(); }
    }

    // On success the record is replaced by HasControl: false; on failure it and the in-memory copy stay.
    async Task<bool> TryRestoreAndClearAsync()
    {
        try
        {
            await RestoreAsync(_data).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error("restore of saved control values", e);
            return false;
        }
        UpdateData(d => d with
        {
            HasControl = false, SavedLidActionAc = null, SavedLidActionDc = null, SavedAutomationEnabled = null,
            SavedPowerModeMappingMode = null, Scheme = null,
        });
        return true;
    }

    void SetHasControl(bool value)
    {
        _hasControl = value;
        Status.HasControl = value;
    }

    // ---- wake-request overrides ---------------------------------------------------------------------------------------

    static readonly string[] WakeRequestTypes = ["DISPLAY", "SYSTEM", "AWAYMODE"];

    // While in control, every program of programs.json that denies wake requests gets `powercfg /requestsoverride PROCESS
    // <exe> DISPLAY SYSTEM AWAYMODE`; the record lists the exes the plugin set, and one that left programs.json is removed.
    public async Task SyncWakeOverridesAsync()
    {
        await _wakeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_data.HasControl) return;
            var desired = WakeOverrides.ExeNames(_programs);
            var recorded = (_data.WakeOverrides ?? []).ToList();
            var current = new List<string>(recorded);
            foreach (var exe in recorded.Where(r => !desired.Contains(r, StringComparer.OrdinalIgnoreCase)).ToArray())
            {
                if (await PowercfgAsync(["/requestsoverride", "PROCESS", exe]).ConfigureAwait(false))
                {
                    current.Remove(exe);
                    Log.Trace($"wake override removed: {exe}");
                }
            }
            foreach (var exe in desired)
            {
                if (!await PowercfgAsync(["/requestsoverride", "PROCESS", exe, .. WakeRequestTypes]).ConfigureAwait(false)) continue;
                if (!current.Contains(exe, StringComparer.OrdinalIgnoreCase)) current.Add(exe);
                Log.Trace($"wake override set: {exe}");
            }
            UpdateData(d => d with { WakeOverrides = current });
        }
        catch (Exception e) { Log.Error("wake overrides", e); }
        finally { _wakeGate.Release(); }
    }

    // Every override the record lists; one that cannot be removed stays recorded.
    async Task RemoveWakeOverridesAsync()
    {
        await _wakeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var left = new List<string>();
            foreach (var exe in _data.WakeOverrides ?? [])
            {
                if (await PowercfgAsync(["/requestsoverride", "PROCESS", exe]).ConfigureAwait(false)) Log.Trace($"wake override removed: {exe}");
                else left.Add(exe);
            }
            UpdateData(d => d with { WakeOverrides = left });
        }
        catch (Exception e) { Log.Error("remove wake overrides", e); }
        finally { _wakeGate.Release(); }
    }

    static async Task<bool> PowercfgAsync(string[] args)
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            p = Process.Start(psi) ?? throw new InvalidOperationException("powercfg did not start");
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            if (p.ExitCode == 0) return true;
            Log.Error($"powercfg {string.Join(' ', args)}", new InvalidOperationException($"exit {p.ExitCode}: {(await output).Trim()} {(await error).Trim()}"));
            return false;
        }
        catch (Exception e)
        {
            Log.Error($"powercfg {string.Join(' ', args)}", e);
            try { if (p is { HasExited: false }) p.Kill(); } catch { /* gone */ }
            return false;
        }
        finally { p?.Dispose(); }
    }

    // ---- LLT settings and the lid action ------------------------------------------------------------------------------

    // automation.json -> <dataDir>\automation-backup-<yyyyMMdd-HHmmss>.json, before anything changes (spec section 7).
    void BackupAutomation()
    {
        var source = Path.Combine(Paths.LltDataDir, "automation.json");
        if (!File.Exists(source)) return;
        Directory.CreateDirectory(_dataDir);
        File.Copy(source, Path.Combine(_dataDir, $"automation-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
    }

    // Idempotent: also run at start when control.json says the plugin holds control.
    async Task AssertControlAsync(ControlData state)
    {
        var automation = IoCContainer.Resolve<AutomationProcessor>();
        var settings = IoCContainer.Resolve<ApplicationSettings>();
        if (automation.IsEnabled) await automation.SetEnabledAsync(false).ConfigureAwait(false);
        if (settings.Store.PowerModeMappingMode != PowerModeMappingMode.Disabled)
        {
            settings.Store.PowerModeMappingMode = PowerModeMappingMode.Disabled;
            settings.SynchronizeStore();
        }
        LidAction.Write(state.Scheme, 0, 0);
    }

    static async Task RestoreAsync(ControlData s)
    {
        var automation = IoCContainer.Resolve<AutomationProcessor>();
        var settings = IoCContainer.Resolve<ApplicationSettings>();
        if (s.SavedAutomationEnabled is { } enabled && automation.IsEnabled != enabled)
            await automation.SetEnabledAsync(enabled).ConfigureAwait(false);
        if (s.SavedPowerModeMappingMode is { } mode && Enum.TryParse<PowerModeMappingMode>(mode, out var parsed)
            && settings.Store.PowerModeMappingMode != parsed)
        {
            settings.Store.PowerModeMappingMode = parsed;
            settings.SynchronizeStore();
        }
        LidAction.Write(s.Scheme, s.SavedLidActionAc, s.SavedLidActionDc);
    }
}

// LIDACTION (SUB_BUTTONS / LIDACTION) of a power scheme: 0 = do nothing.
internal static class LidAction
{
    static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    static readonly Guid LidActionSetting = new("5ca83367-6e45-459f-a27b-476b1d01c936");

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    public static Guid ActiveScheme()
    {
        var rc = PowerGetActiveScheme(IntPtr.Zero, out var p);
        if (rc != 0) throw new InvalidOperationException($"PowerGetActiveScheme failed: {rc}");
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    public static (uint Ac, uint Dc) Read(Guid scheme)
    {
        var sub = SubButtons; var setting = LidActionSetting;
        Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac), "read AC lid action");
        Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc), "read DC lid action");
        return (ac, dc);
    }

    // scheme null: the scheme active now (records written before the scheme was stored).
    public static void Write(Guid? schemeOrNull, uint? ac, uint? dc)
    {
        if (ac is null && dc is null) return;
        var scheme = schemeOrNull ?? ActiveScheme();
        var sub = SubButtons; var setting = LidActionSetting;
        if (ac is { } a) Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, a), "write AC lid action");
        if (dc is { } d) Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, d), "write DC lid action");
        // Re-activating is only needed when the written scheme is the active one; setting it keeps the change live.
        if (ActiveScheme() == scheme) Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme), "apply the scheme");
    }

    static void Check(uint rc, string what)
    {
        if (rc != 0) throw new InvalidOperationException($"{what} failed: {rc}");
    }
}
