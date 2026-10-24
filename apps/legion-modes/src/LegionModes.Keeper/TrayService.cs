using LegionModes.Core.Sensors;
using LegionModes.Tray;

namespace LegionModes.Keeper;

// The two tray temperature icons: a sampling loop on its own thread next to the keeper's supervision loops. A failure here is logged
// and ends only the icons; the keeper keeps supervising LLT.
internal static class TrayService
{
    static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(30);

    // The running sampling loop, for the plugin's live card state.
    public static SensorLoop? Loop { get; private set; }

    // The NVIDIA reads run in this executable started again with the helper argument.
    static System.Diagnostics.ProcessStartInfo HelperStart() =>
        new(Environment.ProcessPath ?? throw new InvalidOperationException("no process path"), Program.MemoryHelper);

    public static Task RunAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var host = new TrayIconHost();
                using var controller = new EcReader();
                var loop = new SensorLoop(controller, new IsolatedMemoryReaderFactory(HelperStart), host, new SystemClock(), KeeperLog.Write);
                KeeperLog.Write("tray icons started");
                Loop = loop;
                try { loop.RunAsync(ct).GetAwaiter().GetResult(); }
                finally { Loop = null; }
                KeeperLog.Write("tray icons stopped");
            }
            catch (Exception e) { KeeperLog.Write("tray icons ended: " + e.Message); }
            if (ct.IsCancellationRequested) break;
            KeeperLog.Write($"tray icons restart in {RestartAfter.TotalSeconds:0} s");   // the thread ended without the keeper being stopped
            try { await Task.Delay(RestartAfter, ct); } catch (OperationCanceledException) { break; }
        }
    });
}
