using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LegionModes.Core.Ipc;

namespace LegionModes.Tests;

public sealed class IpcTests
{
    static string NewName() => "LegionModes.Test." + Guid.NewGuid().ToString("N");
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // The test run is not elevated: the ACL also grants the current user.
    static PipeSecurity TestAcl()
    {
        var s = PipeAcl.Default();
        s.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return s;
    }

    static PipeServer Serve(string name, Func<Message, Task<Message>> handler)
    {
        var server = new PipeServer(name, handler, TestAcl());
        server.Start();
        return server;
    }

    public static IEnumerable<object[]> AllMessages() =>
    [
        [new Heartbeat(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(3)), true, false, true)],
        [new Loaded("2.34.0.0", "1.0.0")],
        [new GracefulExit()],
        [new ExitRequest()],
        [new SelfTestReport(false, ["a", "b"])],
        [new UpdateInProgress(true)],
        [new UpdateRequest("1.2.3")],
        [new CheckRequest()],
        [new KeeperAlive(new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero))],
        [new Ack(false, "boom")],
        [new Ack(true)],
    ];

    [Theory(DisplayName = "@important every message type round-trips through Encode/Decode with equal values")]
    [MemberData(nameof(AllMessages))]
    public void RoundTrip(Message m)
    {
        var back = MessageCodec.Decode(MessageCodec.Encode(m));
        Assert.IsType(m.GetType(), back);
        if (m is SelfTestReport s)
        {
            var r = (SelfTestReport)back;
            Assert.Equal(s.Ok, r.Ok);
            Assert.Equal(s.Failures, r.Failures);
        }
        else Assert.Equal(m, back);
    }

    [Fact(DisplayName = "@important Decode rejects unknown type, missing type or body, and truncated or malformed JSON with InvalidDataException")]
    public void DecodeRejects()
    {
        static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(B("""{"type":"Nope"}""")));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(B("""{"type":"Nope","body":{}}""")));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(B("""{"body":{}}""")));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(B("""{"type":"Ack"}""")));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(B("not json")));
        var good = MessageCodec.Encode(new Loaded("a", "b"));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(good.AsSpan(0, good.Length - 5)));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode([]));
    }

    [Fact(DisplayName = "@important a PipeServer answers a PipeClient with the handler's reply; two parallel clients both get replies")]
    public async Task ServerAnswers()
    {
        var name = NewName();
        await using var server = Serve(name, async m =>
        {
            await Task.Delay(100);
            return new Ack(true, ((UpdateRequest)m).Version);
        });
        var one = PipeClient.SendAsync(name, new UpdateRequest("one"), Wait);
        var two = PipeClient.SendAsync(name, new UpdateRequest("two"), Wait);
        var replies = await Task.WhenAll(one, two);
        Assert.Equal(new Ack(true, "one"), replies[0]);
        Assert.Equal(new Ack(true, "two"), replies[1]);
    }

    [Fact(DisplayName = "@important a handler that outlasts the 5 s connection bound still gets its reply to the client")]
    public async Task SlowHandlerStillReplies()
    {
        var name = NewName();
        await using var server = Serve(name, async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5.5));
            return new Ack(true, "slow");
        });
        Assert.Equal(new Ack(true, "slow"), await PipeClient.SendAsync(name, new CheckRequest(), TimeSpan.FromSeconds(20)));
    }

    [Fact(DisplayName = "@important SendAsync to a name with no server returns null within the timeout")]
    public async Task NoServer()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await PipeClient.SendAsync(NewName(), new CheckRequest(), TimeSpan.FromMilliseconds(200));
        Assert.Null(r);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact(DisplayName = "@important a handler exception becomes Ack(false, message) and the server keeps serving")]
    public async Task HandlerThrows()
    {
        var name = NewName();
        await using var server = Serve(name, m => m is CheckRequest
            ? throw new InvalidOperationException("bad handler")
            : Task.FromResult<Message>(new Ack(true)));
        Assert.Equal(new Ack(false, "bad handler"), await PipeClient.SendAsync(name, new CheckRequest(), Wait));
        Assert.Equal(new Ack(true), await PipeClient.SendAsync(name, new GracefulExit(), Wait));
    }

    [Fact(DisplayName = "@important a handler throwing InvalidDataException still gets Ack(false, ...) to the client")]
    public async Task HandlerThrowsInvalidData()
    {
        var name = NewName();
        await using var server = Serve(name, _ => throw new InvalidDataException("from handler"));
        Assert.Equal(new Ack(false, "from handler"), await PipeClient.SendAsync(name, new CheckRequest(), Wait));
    }

    [Fact(DisplayName = "@important Start throws when the pipe name is already served")]
    public async Task SecondServerSameName()
    {
        var name = NewName();
        await using var first = Serve(name, _ => Task.FromResult<Message>(new Ack(true)));
        await using var second = new PipeServer(name, _ => Task.FromResult<Message>(new Ack(true)), TestAcl());
        Assert.ThrowsAny<Exception>(() => second.Start());
        Assert.Equal(new Ack(true), await PipeClient.SendAsync(name, new CheckRequest(), Wait));
    }

    [Fact(DisplayName = "@important an oversized or truncated frame is dropped without a reply and the server keeps serving")]
    public async Task BadFramesDropped()
    {
        var name = NewName();
        await using var server = Serve(name, _ => Task.FromResult<Message>(new Ack(true)));

        using (var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await c.ConnectAsync(5000);
            await c.WriteAsync(BitConverter.GetBytes(2 * 1024 * 1024));   // above 1 MiB
            await c.FlushAsync();
            Assert.Equal(0, await c.ReadAsync(new byte[16]));   // closed, no reply
        }
        using (var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await c.ConnectAsync(5000);
            await c.WriteAsync(new byte[] { 100, 0, 0, 0, (byte)0x7B });   // promises 100 bytes, sends 1, then ends
            await c.FlushAsync();
            c.Dispose();
        }
        Assert.Equal(new Ack(true), await PipeClient.SendAsync(name, new CheckRequest(), Wait));
    }

    [Fact(DisplayName = "@important PipeAcl.Default grants exactly SYSTEM FullControl and Administrators ReadWrite")]
    public void DefaultAcl()
    {
        var rules = PipeAcl.Default().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Assert.Equal(PipeAccessRights.FullControl, rules.Single(r => r.IdentityReference.Equals(system)).PipeAccessRights);
        // Windows adds Synchronize to a granted mask.
        var adminRights = rules.Single(r => r.IdentityReference.Equals(admins)).PipeAccessRights;
        Assert.Equal(PipeAccessRights.ReadWrite, adminRights & ~PipeAccessRights.Synchronize);
    }
}
