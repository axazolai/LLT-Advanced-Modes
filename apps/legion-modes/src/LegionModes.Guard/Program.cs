using System.ComponentModel;
using LegionModes.Core;
using LegionModes.Core.Ipc;

namespace LegionModes.Guard;

internal static class Program
{
    static int Main(string[] args)
    {
#if DEBUG
        if (args.Contains("--send-heartbeat")) return SendHeartbeat();   // Debug smoke helper
#endif
        var console = args.Contains("--console");
        var devPipeUser = false;
#if DEBUG
        devPipeUser = Environment.GetEnvironmentVariable("LEGIONMODES_DEV_PIPE_USER") == "1";
#endif
        var guard = new Guard(new GuardActions(console), devPipeUser);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => GuardLog.Write("unhandled: " + e.ExceptionObject);
        if (console)
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; guard.Stop(); };
            ConsoleLidWindow.Start(guard);
            guard.Run();
            ConsoleLidWindow.Close();
            return 0;
        }
        try { ServiceHost.Run(guard); return 0; }
        catch (Win32Exception e) when (e.NativeErrorCode == 1063)
        {
            Console.Error.WriteLine("Not started by the service control manager. Use --console to run in a console.");
            return 1;
        }
    }

#if DEBUG
    static int SendHeartbeat()
    {
        var reply = PipeClient.SendAsync(Paths.GuardPipe, new Heartbeat(DateTimeOffset.Now, true, false, true), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Console.WriteLine("reply: " + (reply is null ? "none" : reply.ToString()));
        return reply is Ack { Ok: true } ? 0 : 1;
    }
#endif
}
