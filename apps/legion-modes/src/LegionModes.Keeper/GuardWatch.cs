using System.ServiceProcess;
using LegionModes.Core;
using LegionModes.Core.Ipc;

namespace LegionModes.Keeper;

internal static class GuardWatch
{
    static readonly TimeSpan Period = TimeSpan.FromSeconds(60);

    public static async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception e) { KeeperLog.Write("guard watch failed: " + e.Message); }
            try { await PipeClient.SendAsync(Paths.GuardPipe, new KeeperAlive(DateTimeOffset.Now), TimeSpan.FromSeconds(3)); }
            catch { /* SendAsync does not throw; belt and braces */ }
            try { await Task.Delay(Period, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    static void Tick()
    {
        try
        {
            using var service = new ServiceController(Paths.GuardService);
            var status = service.Status;   // throws InvalidOperationException when the service does not exist
            if (status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return;
            KeeperLog.Write($"guard service is {status}; starting it");
            service.Start();
        }
        catch (InvalidOperationException e)
        {
            KeeperLog.Write($"guard service {Paths.GuardService} unavailable: {e.Message}");
        }
    }
}
