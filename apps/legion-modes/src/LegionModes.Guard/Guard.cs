using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LegionModes.Core;
using LegionModes.Core.Ipc;
using LegionModes.Core.Status;
using LegionModes.Core.Supervision;

namespace LegionModes.Guard;

// Decisions run on one 5 s loop thread (Run); every state change goes through _lock.
internal sealed class Guard(GuardActions actions, bool devPipeUser)
{
    const int WTS_SESSION_LOGON = 5;
    static readonly TimeSpan Period = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PipeFailureNotifyAfter = TimeSpan.FromSeconds(60);

    readonly object _lock = new();
    readonly ManualResetEventSlim _stop = new(false);
    Heartbeat? _heartbeat;
    DateTimeOffset? _updateSince, _resumedAt, _lidClosedAt, _taskRunAt, _directStartAt, _slept, _missingSince;
    bool _lidClosed;
    PipeServer? _server;                        // loop thread only
    DateTimeOffset? _pipeFailedSince;
    bool _pipeNotified;
    string _lastTick = "";

    public void Stop() => _stop.Set();

    public void SessionChanged(int eventType)
    {
        if (eventType == WTS_SESSION_LOGON)
        {
            // A new logon: the keeper's logon task starts it; give the cycle a fresh start.
            lock (_lock) { _taskRunAt = null; _directStartAt = null; _missingSince = null; }
        }
        GuardLog.Write("session change " + eventType);
    }

    public void Resumed()
    {
        lock (_lock) _resumedAt = DateTimeOffset.Now;
        GuardLog.Write("resumed from sleep");
    }

    public void Lid(bool open)
    {
        lock (_lock)
        {
            if (open) { _lidClosed = false; _lidClosedAt = null; }
            else if (!_lidClosed) { _lidClosed = true; _lidClosedAt = DateTimeOffset.Now; }
        }
        GuardLog.Write("lid " + (open ? "open" : "closed"));
    }

    public void Run()
    {
        GuardLog.Write($"guard started (console={actions.Console}, data {Paths.DataRoot()})");
        if (!actions.Console && !Paths.DataRootOverridden)
        {
            try { DataRootSecurity.Apply(Paths.DataRoot()); }
            catch (Exception e) { GuardLog.Write("cannot protect the data root: " + e.Message); }
        }
        do
        {
            try { EnsurePipe(); }
            catch (Exception e) { GuardLog.Write("pipe check failed: " + e.Message); }
            try { Tick(); }
            catch (Exception e) { GuardLog.Write("tick failed: " + e); }
        }
        while (!_stop.Wait(Period));
        if (_server is not null) _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GuardLog.Write("guard stopped");
    }

    // Retried every tick until it succeeds (a squatted name or a transient error must not disable the pipe for good).
    void EnsurePipe()
    {
        if (_server is not null) return;
        try
        {
            PipeSecurity? acl = null;   // null: the production ACL (SYSTEM + Administrators)
#if DEBUG
            if (devPipeUser) acl = DevAcl();
#else
            _ = devPipeUser;
#endif
            var server = new PipeServer(Paths.GuardPipe, HandleAsync, acl);
            server.Start();
            _server = server;
            if (_pipeFailedSince is not null) GuardLog.Write("pipe server started after earlier failures");
            else GuardLog.Write("pipe server started");
            _pipeFailedSince = null;
            _pipeNotified = false;
        }
        catch (Exception e)
        {
            var now = DateTimeOffset.Now;
            if (_pipeFailedSince is null) { _pipeFailedSince = now; GuardLog.Write("pipe server did not start (retrying every tick): " + e.Message); }
            else if (!_pipeNotified && now - _pipeFailedSince >= PipeFailureNotifyAfter)
            {
                _pipeNotified = true;
                actions.Warn("pipe-down", "the guard pipe has not started for over a minute: " + e.Message);
            }
        }
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

    Task<Message> HandleAsync(Message message)
    {
        Message reply;
        lock (_lock)
        {
            switch (message)
            {
                case Heartbeat h:
                    _heartbeat = h with { At = DateTimeOffset.Now };   // receipt time: the sender's clock is not trusted
                    reply = new Ack(true);
                    break;
                case UpdateInProgress u:
                    _updateSince = u.Active ? DateTimeOffset.Now : null;
                    reply = new Ack(true);
                    break;
                case KeeperAlive:
                    reply = new Ack(true);
                    break;
                default:
                    reply = new Ack(false, "unsupported message " + message.GetType().Name);
                    break;
            }
        }
        return Task.FromResult(reply);
    }

    void Tick()
    {
        var now = DateTimeOffset.Now;
        // A failing probe must not stop the sleep decision: no session / keeper assumed running means no keeper action.
        var session = false;
        var running = true;
        try { session = actions.UserSessionActive(); } catch (Exception e) { GuardLog.Write("session probe failed: " + e.Message); }
        try { running = actions.KeeperRunning(); } catch (Exception e) { GuardLog.Write("keeper probe failed: " + e.Message); }
        GuardState state;
        lock (_lock)
        {
            if (running) { _taskRunAt = null; _directStartAt = null; _missingSince = null; }
            else _missingSince ??= now;
            state = new GuardState(session, running, _missingSince, _taskRunAt, _lidClosed, _lidClosedAt,
                _heartbeat, _updateSince, _resumedAt, _slept, _directStartAt);
        }
        var decided = GuardPolicy.Decide(state, now);
        var line = $"session={session} keeper={running} lidClosed={state.LidClosed} heartbeat={(state.LastHeartbeat is null ? "none" : "yes")} -> " +
                   (decided.Count == 0 ? "none" : string.Join(",", decided));
        if (line != _lastTick) { GuardLog.Write("tick: " + line); _lastTick = line; }
        foreach (var action in decided)
        {
            switch (action)
            {
                case GuardAction.RunKeeperTask:
                    lock (_lock) _taskRunAt = DateTimeOffset.Now;
                    actions.RunKeeperTask();
                    break;
                case GuardAction.StartKeeperDirect:
                    lock (_lock) _directStartAt = DateTimeOffset.Now;
                    actions.StartKeeperDirect();
                    break;
                case GuardAction.SleepNow:
                    lock (_lock) _slept = DateTimeOffset.Now;
                    actions.SleepNow();
                    break;
            }
        }
    }
}
