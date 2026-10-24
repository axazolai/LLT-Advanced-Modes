using System.Diagnostics;
using LegionModes.Core.Sensors;
using LegionModes.Tray;
using Xunit;

namespace LegionModes.Tests;

public class IsolatedReaderTests
{
    private static ProcessStartInfo Child(string command) =>
        new("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"");

    [Fact(DisplayName = "@important a reading goes through the helper pipe and comes back whole, missing values stay missing")]
    public void ReadingRoundTrip()
    {
        using var reader = new IsolatedMemoryReader(Child("while (($l = [Console]::In.ReadLine()) -ne $null) { '52;-' }"));
        Assert.Equal(new GpuReading(52, null), reader.Read());
        Assert.Equal(new GpuReading(52, null), reader.Read());
    }

    [Fact(DisplayName = "@critical a helper that dies under a read is an error of that read and not of the keeper; the next helper reads again")]
    public void HelperDeathIsAnException()
    {
        using var dying = new IsolatedMemoryReader(Child("exit 3"));
        Assert.Throws<IOException>(() => dying.Read());
        using var next = new IsolatedMemoryReader(Child("while (($l = [Console]::In.ReadLine()) -ne $null) { '40;9' }"));
        Assert.Equal(new GpuReading(40, 9), next.Read());
    }

    [Fact(DisplayName = "@important the helper loop answers every read, answers 'no values' when the card read throws, and ends when the pipe closes")]
    public void HelperLoop()
    {
        var calls = 0;
        var fake = new Fake(() => ++calls == 2 ? throw new InvalidOperationException("card gone") : new GpuReading(61, 12));
        var output = new StringWriter { NewLine = "\n" };
        var code = MemoryHelperProtocol.RunHelper(new StringReader("read\nread\nread\n"), output, () => fake);
        Assert.Equal(0, code);
        Assert.Equal("61;12\n-;-\n61;12\n", output.ToString());
        Assert.Equal(0, fake.Disposed);   // closing the library would wake a sleeping card
    }

    private sealed class Fake(Func<GpuReading> read) : IMemoryTempReader
    {
        public int Disposed;
        public GpuReading Read() => read();
        public void Dispose() => Disposed++;
    }
}
