using System.Diagnostics;
using System.Globalization;
using LegionModes.Core.Sensors;

namespace LegionModes.Tray;

// The NVIDIA stack runs in a helper process of its own: a native fault in it (seen when the card is ejected while a call is under way)
// ends the helper and not the keeper. The helper holds the library open between reads, as the keeper did before; killing it drops the
// library without closing it, so a sleeping card is not woken.
public sealed class IsolatedMemoryReader : IMemoryTempReader
{
    public static readonly TimeSpan ReplyWait = TimeSpan.FromSeconds(15);

    readonly Process _child;

    public IsolatedMemoryReader(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        _child = Process.Start(start) ?? throw new InvalidOperationException("the memory helper did not start");
    }

    public GpuReading Read()
    {
        try
        {
            _child.StandardInput.WriteLine(MemoryHelperProtocol.ReadCommand);
            _child.StandardInput.Flush();
            var reply = _child.StandardOutput.ReadLineAsync();
            if (!reply.Wait(ReplyWait)) throw new TimeoutException("the memory helper did not answer");
            return MemoryHelperProtocol.Parse(reply.Result ?? throw new IOException("the memory helper ended"));
        }
        catch (Exception e) when (e is not TimeoutException)
        {
            throw new IOException("the memory helper failed: " + e.Message, e);
        }
    }

    public void Dispose()
    {
        try { if (!_child.HasExited) _child.Kill(); } catch { /* it is gone already */ }
        _child.Dispose();
    }
}

public sealed class IsolatedMemoryReaderFactory(Func<ProcessStartInfo> start) : IMemoryTempReaderFactory
{
    public IMemoryTempReader Open() => new IsolatedMemoryReader(start());
}

// One line per request: the keeper sends "read", the helper answers "<memory C or ->;<load % or ->".
public static class MemoryHelperProtocol
{
    public const string ReadCommand = "read";

    public static string Format(GpuReading r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.MemoryC?.ToString(CultureInfo.InvariantCulture) ?? "-"};{r.LoadPercent?.ToString(CultureInfo.InvariantCulture) ?? "-"}");

    public static GpuReading Parse(string line)
    {
        var parts = line.Split(';');
        if (parts.Length != 2) throw new FormatException("memory helper reply: " + line);
        return new GpuReading(Value(parts[0]), Value(parts[1]));

        static int? Value(string s) => s == "-" ? null : int.Parse(s, CultureInfo.InvariantCulture);
    }

    // The helper's loop: ends when the keeper closes the pipe or dies. A reading that throws is answered as "no values".
    public static int RunHelper(TextReader input, TextWriter output, Func<IMemoryTempReader> open)
    {
        var reader = open();   // never closed: closing the library wakes a sleeping card, the process end drops it
        while (input.ReadLine() is { } command)
        {
            if (command != ReadCommand) continue;
            GpuReading reading;
            try { reading = reader.Read(); }
            catch { reading = new GpuReading(null, null); }
            output.WriteLine(Format(reading));
            output.Flush();
        }
        return 0;
    }
}
