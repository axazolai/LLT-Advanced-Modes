using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LegionModes.Core.Ipc;

public static class PipeAcl
{
    // SYSTEM and Administrators only; plugin and keeper run elevated, guard runs as SYSTEM.
    public static PipeSecurity Default()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    // Default plus the interactive owner (ReadWrite only: the owner cannot create a pipe instance).
    public static PipeSecurity Quick(SecurityIdentifier owner)
    {
        var security = Default();
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}

static class Framing
{
    public const int MaxFrame = 1024 * 1024;

    public static async Task WriteAsync(Stream s, Message m, CancellationToken ct)
    {
        var json = MessageCodec.Encode(m);
        var buf = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf, json.Length);
        json.CopyTo(buf, 4);
        await s.WriteAsync(buf, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    // Returns null when the length prefix is out of range; throws EndOfStreamException when the stream ends early.
    public static async Task<byte[]?> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[4];
        await s.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len < 0 || len > MaxFrame) return null;
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }
}

public sealed class PipeServer : IAsyncDisposable
{
    readonly string _name;
    readonly Func<Message, Task<Message>> _handler;
    readonly PipeSecurity _security;
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new();
    readonly HashSet<Task> _inFlight = [];
    Task? _loop;

    public PipeServer(string name, Func<Message, Task<Message>> handler, PipeSecurity? security = null)
    {
        _name = name;
        _handler = handler;
        // Own copy: the caller's object is never mutated.
        _security = new PipeSecurity();
        _security.SetSecurityDescriptorBinaryForm((security ?? PipeAcl.Default()).GetSecurityDescriptorBinaryForm());
        // Every instance after the first is access-checked against the DACL; Administrators need CreateNewInstance
        // so an elevated server can keep a listening instance alive while one is handled.
        _security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
    }

    NamedPipeServerStream Create(bool first = false) => NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message,
        first ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance : PipeOptions.Asynchronous, 0, 0, _security);

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("already started");
        var first = Create(first: true);   // throws when the name is taken; listening before Start returns
        _loop = Task.Run(() => AcceptLoop(first));
    }

    async Task AcceptLoop(NamedPipeServerStream pipe)
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                if (ct.IsCancellationRequested) return;
                pipe = await CreateNext(ct).ConfigureAwait(false) ?? throw new OperationCanceledException();
                continue;
            }
            var connected = pipe;
            var next = await CreateNext(ct).ConfigureAwait(false);   // listening again before handling this one
            if (next is null) { connected.Dispose(); return; }
            pipe = next;
            var gate = new TaskCompletionSource();
            var t = Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                await Handle(connected, ct).ConfigureAwait(false);
            });
            lock (_gate) _inFlight.Add(t);
            _ = t.ContinueWith(done => { lock (_gate) _inFlight.Remove(done); });
            gate.SetResult();
        }
        pipe.Dispose();
    }

    async Task<NamedPipeServerStream?> CreateNext(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { return Create(); }
            catch { await Task.Delay(100, CancellationToken.None).ConfigureAwait(false); }
        }
        return null;
    }

    static readonly TimeSpan ConnectionBound = TimeSpan.FromSeconds(5);

    async Task Handle(NamedPipeServerStream pipe, CancellationToken serverCt)
    {
        try
        {
            using (pipe)
            {
                using var bound = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
                bound.CancelAfter(ConnectionBound);   // read and reply write; the handler itself is not bounded
                var ct = bound.Token;
                var bytes = await Framing.ReadAsync(pipe, ct).ConfigureAwait(false);
                if (bytes is null) return;
                Message request;
                try { request = MessageCodec.Decode(bytes); }
                catch (InvalidDataException) { return; }
                Message reply;
                try { reply = await _handler(request).ConfigureAwait(false); }
                catch (Exception e) { reply = new Ack(false, e.Message); }
                // A handler may outlast the read bound, which would leave `ct` cancelled: the reply gets its own bound.
                using var replyBound = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
                replyBound.CancelAfter(ConnectionBound);
                await Framing.WriteAsync(pipe, reply, replyBound.Token).ConfigureAwait(false);
                try { pipe.WaitForPipeDrain(); } catch { /* peer already gone */ }
            }
        }
        catch { /* dropped connection: nothing to reply to */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        Task[] pending;
        lock (_gate) pending = _inFlight.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

public static class PipeClient
{
    public static async Task<Message?> SendAsync(string name, Message m, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            await Framing.WriteAsync(client, m, cts.Token).ConfigureAwait(false);
            var bytes = await Framing.ReadAsync(client, cts.Token).ConfigureAwait(false);
            return bytes is null ? null : MessageCodec.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }
}
