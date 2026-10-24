using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Ipc;
using LegionModes.Core.Status;
using LegionModes.Core.Supervision;
using LegionModes.Tray;
using LegionModes.Updates;

namespace LegionModes.Keeper;

internal static class Program
{
    const string SingleInstanceName = @"Global\LegionModes.Keeper";
    internal const string MemoryHelper = "--memory-helper";

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);

    const uint NoFaultDialogs = 0x0001 | 0x0002 | 0x8000;   // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX

    static async Task<int> Main(string[] args)
    {
        SetErrorMode(NoFaultDialogs);   // a fault ends the process quietly; the guard restarts the keeper, the keeper restarts the helper
        if (args.Contains(MemoryHelper)) return MemoryHelperProtocol.RunHelper(Console.In, Console.Out, () => new LhmMemoryReader());
        Tr.FromLlt();
        if (args.Contains("--check-now")) return await CheckNowAsync();

        using var single = new Mutex(false, SingleInstanceName);
        try { if (!single.WaitOne(0)) return 0; }   // never released: the process end abandons it
        catch (AbandonedMutexException) { /* the previous keeper died; this one now owns the mutex */ }

        // The tray icons are registered under a GUID tied to the executable path: from any other folder they may never appear.
        if (!Paths.DataRootOverridden && Environment.ProcessPath is { } exe
            && !string.Equals(Path.GetDirectoryName(exe), Paths.InstallDir, StringComparison.OrdinalIgnoreCase))
            KeeperLog.Write($"keeper runs from {exe}, not from {Paths.InstallDir}: the tray icons are tied to the executable path and may not appear");

        // The data root holds installers that run elevated: only SYSTEM and Administrators may write there.
        if (!Paths.DataRootOverridden)
        {
            try { DataRootSecurity.Apply(Paths.DataRoot()); }
            catch (Exception e)
            {
                KeeperLog.Write("cannot protect the data root; keeper not started: " + e.Message);
                return 1;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => KeeperLog.Write("unhandled: " + e.ExceptionObject);

        // Smoke-run switches exist in Debug builds only: no LLT supervision; a pipe ACL that also admits the current user.
        var noLlt = false;
        var devPipeUser = false;
#if DEBUG
        noLlt = Environment.GetEnvironmentVariable("LEGIONMODES_NO_LLT") == "1";
        devPipeUser = Environment.GetEnvironmentVariable("LEGIONMODES_DEV_PIPE_USER") == "1";
#endif
        return await new KeeperService(noLlt, devPipeUser).RunAsync(cts.Token);
    }

    // Sends CheckRequest to the running keeper and waits for the check to finish (a download and an extraction).
    static async Task<int> CheckNowAsync()
    {
        AttachConsole(-1);   // a WinExe has no console of its own; print into the parent's when there is one
        // The pipe client waits for a missing server until its timeout, so a running keeper is detected by its mutex.
        try
        {
            if (!Mutex.TryOpenExisting(SingleInstanceName, out var running))
            {
                Console.WriteLine("keeper not running");
                return 1;
            }
            running.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine("needs elevation");
            return 2;
        }
        var reply = await PipeClient.SendAsync(Paths.KeeperPipe, new CheckRequest(), TimeSpan.FromMinutes(10));
        switch (reply)
        {
            case null:
                Console.WriteLine("keeper not running");
                return 1;
            case Ack { Ok: true }:
                Console.WriteLine("check done");
                return 0;
            case Ack ack:
                Console.WriteLine("check failed: " + ack.Error);
                return 2;
            default:
                Console.WriteLine("unexpected reply " + reply.GetType().Name);
                return 2;
        }
    }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

internal sealed class KeeperService
{
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
    static readonly TimeSpan RetryAfterError = TimeSpan.FromHours(1);
    static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);

    readonly bool _noLlt;
    readonly bool _devPipeUser;
    readonly KeeperPolicy _policy = new(new SystemClock());
    readonly LltSupervisor _supervisor;
    readonly UpdateOps _ops;
    readonly UpdateOrchestrator _orchestrator;
    readonly KeeperState _state;
    readonly SemaphoreSlim _gate = new(1, 1);
    CancellationToken _stopping;

    public KeeperService(bool noLlt, bool devPipeUser)
    {
        _noLlt = noLlt;
        _devPipeUser = devPipeUser;
        _supervisor = new LltSupervisor(_policy, text => Alert(text, true));
        var http = ReleaseClient.CreateHttp();
        _state = new KeeperState(UpdateOps.ReadInstalledVersion());
        _ops = new UpdateOps(new ReleaseClient(http), http, _state, text => Alert(text, true));
        _orchestrator = new UpdateOrchestrator(_ops);
    }

    // Log, Application event log and the plugin's pending notifications.
    void Alert(string text, bool error)
    {
        KeeperLog.Write("ALERT " + text);
        KeeperLog.Event(text, error ? EventLogEntryType.Error : EventLogEntryType.Warning);
        try { PendingNotifications.Append(Path.Combine(Paths.DataRoot(), "notifications-pending.json"), $"{DateTimeOffset.Now:u} keeper: {text}"); }
        catch (Exception e) { KeeperLog.Write("notification failed: " + e.Message); }
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        _stopping = ct;
        KeeperLog.Write($"keeper started (installed LLT {_ops.InstalledVersion()}, noLlt={_noLlt}, data {Paths.DataRoot()})");
        var earliest = DateTimeOffset.Now + FirstCheckDelay;   // a persisted later NextCheck is honoured
        _state.Update(u => u with { NextCheck = u.NextCheck is { } persisted && persisted > earliest ? persisted : earliest });

        PipeServer? server = null;
        try
        {
            PipeSecurity? acl = null;   // null: the production ACL (SYSTEM + Administrators)
#if DEBUG
            if (_devPipeUser) acl = DevAcl();
#endif
            server = new PipeServer(Paths.KeeperPipe, HandleAsync, acl);
            server.Start();
        }
        catch (Exception e)
        {
            KeeperLog.Write("pipe server did not start: " + e.Message);
            server = null;
        }

        var loops = new List<Task> { GuardWatch.RunAsync(ct), CheckLoopAsync(ct), TrayService.RunAsync(ct) };
        if (!_noLlt) loops.Add(_supervisor.RunAsync(ct));
        try { await Task.WhenAll(loops); }
        catch (Exception e) { KeeperLog.Write("loop ended: " + e.Message); }
        if (server is not null) await server.DisposeAsync();
        KeeperLog.Write("keeper stopped");
        return 0;
    }

#if DEBUG
    static PipeSecurity DevAcl()
    {
        var s = PipeAcl.Default();
        s.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));   // Debug only: the unelevated smoke user must create the next listening instance
        return s;
    }
#endif

    async Task<Message> HandleAsync(Message message)
    {
        switch (message)
        {
            case GracefulExit:
                _supervisor.NoteGracefulExit();
                KeeperLog.Write("GracefulExit received");
                return new Ack(true);
            case Loaded l:
                KeeperLog.Write($"Loaded: LLT {l.LltVersion}, plugin {l.PluginVersion}");
                _ops.OnLoaded();
                return new Ack(true);
            case SelfTestReport r:
                KeeperLog.Write($"SelfTestReport: ok={r.Ok} {string.Join("; ", r.Failures)} | warnings: {string.Join("; ", r.Warnings ?? [])}");
                _ops.OnSelfTest(r);
                return new Ack(true);
            case CheckRequest:
                var outcome = await RunCheckAsync(_stopping);
                return outcome.Error is null ? new Ack(true) : new Ack(false, outcome.Error);
            case UpdateRequest u:
                return StartUpdate(u.Version);
            case ReleaseGpu release:
                if (TrayService.Loop is not { } loop) return new Ack(false, "the tray loop is not running");
                loop.ReleaseCard(TimeSpan.FromSeconds(Math.Clamp(release.Seconds, 1, 300)));
                return new Ack(true);
            case GpuLiveRequest:
                if (TrayService.Loop is not { } sensors) return new Ack(false, "the tray loop is not running");
                sensors.Watch();
                var g = sensors.Latest;
                return new GpuLiveReply(g.Awake, g.TempC, g.MemoryC, g.LoadPercent, DateTimeOffset.Now);
            default:
                return new Ack(false, "unsupported message " + message.GetType().Name);
        }
    }

    async Task<CheckOutcome> RunCheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var outcome = await _orchestrator.CheckAsync(ct);
            var now = DateTimeOffset.Now;
            _state.LastOutcome = outcome;
            _state.Update(u => u with
            {
                LastCheck = now,
                NextCheck = now + (outcome.Error is null ? CheckEvery : RetryAfterError),
                InstalledVersion = _ops.InstalledVersion(),
                Last = KeeperState.Summarize(outcome),
            });
            KeeperLog.Write(outcome.Error is not null ? "check failed: " + outcome.Error
                : outcome.Release is null ? "check: no newer release"
                : $"check: {outcome.Release.Version} compatible={outcome.Report?.Compatible}");
            return outcome;
        }
        finally { _gate.Release(); }
    }

    async Task CheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.Now >= _state.Updates.NextCheck)
                    await RunCheckAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                KeeperLog.Write("check loop error: " + e.Message);
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    // LLT is pinned (llt.lock.json): a newer version is reported by the check, installed only from the project after an analysis there.
    static Message StartUpdate(string requested) =>
        new Ack(false, $"LLT is pinned to {Pins.LltVersion}; {requested} is installed only from the project after its analysis");
}
