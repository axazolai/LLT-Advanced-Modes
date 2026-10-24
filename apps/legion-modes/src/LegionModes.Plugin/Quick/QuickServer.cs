using Message = LegionModes.Core.Ipc.Message;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LegionModes.Core;
using LegionModes.Core.Ipc;
using LegionModes.Core.Quick;

namespace LegionModes.Plugin.Quick;

// The quick pipe: open to SYSTEM, Administrators and the interactive owner (the desktop menu runs non-elevated). It accepts
// QuickAction only; any other message is refused and logged.
internal sealed class QuickServer : IAsyncDisposable
{
    const uint NoSession = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    readonly PipeServer _pipe;

    QuickServer(PipeServer pipe) => _pipe = pipe;

    // Never throws; null when the pipe could not be opened (logged).
    public static QuickServer? TryStart(Func<QuickActionKind, QuickResult> handle)
    {
        try
        {
            var pipe = new PipeServer(Paths.QuickPipe, m => Task.FromResult(Handle(m, handle)), PipeAcl.Quick(InteractiveUserSid()));
            pipe.Start();
            return new QuickServer(pipe);
        }
        catch (Exception e)
        {
            Log.Error("quick pipe", e);
            return null;
        }
    }

    internal static Message Handle(Message m, Func<QuickActionKind, QuickResult> handle)
    {
        try
        {
            if (m is not QuickAction action)
            {
                Log.Trace($"quick pipe: rejected a {m.GetType().Name} message");
                return new Ack(false, "rejected");
            }
            var result = handle(action.Kind);
            return result.Handled && result.Error is null ? new Ack(true) : new Ack(false, result.Error ?? Tr.T("not done"));
        }
        catch (Exception e)
        {
            Log.Error("quick pipe handler", e);
            return new Ack(false, e.Message);
        }
    }

    // The user of the active console session (WTSQueryUserToken needs the TCB privilege: the plugin has it only when it runs
    // as SYSTEM); otherwise the user the plugin runs as, which is the owner when LLT is elevated through the owner's account.
    static SecurityIdentifier InteractiveUserSid()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session != NoSession)
        {
            if (WTSQueryUserToken(session, out var token))
            {
                try
                {
                    using var identity = new WindowsIdentity(token);
                    if (identity.User is { } sid) return sid;
                }
                finally { CloseHandle(token); }
            }
            else Log.Trace($"quick pipe: WTSQueryUserToken failed ({Marshal.GetLastWin32Error()}); using the plugin's own user");
        }
        return WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("no user SID for the quick pipe");
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}
