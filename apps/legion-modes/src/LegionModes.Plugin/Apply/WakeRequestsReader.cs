using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using LegionModes.Core.Programs;

namespace LegionModes.Plugin.Apply;

// `powercfg /requests`: who holds the display or the system awake right now. Needs administrator rights (LLT runs elevated).
public static class WakeRequestsReader
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Never throws: a failure is logged and gives an empty list.
    public static async Task<IReadOnlyList<WakeRequest>> ReadAsync()
    {
        try
        {
            var text = await RunAsync().ConfigureAwait(false);
            return text is null ? [] : WakeRequests.Parse(text);
        }
        catch (Exception e)
        {
            Log.Error("powercfg /requests", e);
            return [];
        }
    }

    static async Task<string?> RunAsync()
    {
        // powercfg prints in the console's OEM code page (process names can be Cyrillic).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
            Arguments = "/requests",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
        });
        if (p is null) return null;
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            var output = p.StandardOutput.ReadToEndAsync(cts.Token);
            var error = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                Log.Error("powercfg /requests", new InvalidOperationException($"exit {p.ExitCode}: {(await error.ConfigureAwait(false)).Trim()}"));
                return null;
            }
            return text;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            throw new TimeoutException("powercfg /requests did not finish in 10 s");
        }
    }
}
