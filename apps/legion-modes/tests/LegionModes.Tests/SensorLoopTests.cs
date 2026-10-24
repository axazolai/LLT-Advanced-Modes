using LegionModes.Core.Sensors;
using Xunit;

namespace LegionModes.Tests;

public class SensorLoopTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    private sealed class FakeController : IControllerReader
    {
        public Func<ControllerSample> Next = () => new ControllerSample(50, 40, 0, 1800, 0);
        public readonly List<bool> Asked = new();
        public int Resets;
        public ControllerSample Read(bool includeDgpu)
        {
            Asked.Add(includeDgpu);
            var s = Next();
            return includeDgpu ? s : s with { DgpuC = null };
        }
        public void Reset() => Resets++;
    }

    private sealed class FakeReader : IMemoryTempReader
    {
        public int Reads;
        public int Disposed;
        public Func<int?> Value = () => 52;
        public Func<int?> Load = () => 0;
        public GpuReading Read() { Reads++; return new GpuReading(Value(), Load()); }
        public void Dispose() => Disposed++;
    }

    private sealed class FakeFactory : IMemoryTempReaderFactory
    {
        public readonly List<FakeReader> Readers = new();
        public bool ThrowOnOpen;
        public Func<int?> Load = () => 0;
        public Action<int, FakeReader>? Configure;   // (index of the reader, the reader)
        public int Opens;
        public IMemoryTempReader Open()
        {
            Opens++;
            if (ThrowOnOpen) throw new InvalidOperationException("no nvidia");
            var r = new FakeReader { Load = Load };
            Configure?.Invoke(Readers.Count, r);
            Readers.Add(r);
            return r;
        }
    }

    private sealed class FakeView : ITrayView
    {
        public readonly List<(Sensor Sensor, IconState State)> Shown = new();
        public readonly List<Sensor> Hidden = new();
        public IconState? Last(Sensor s) => Shown.LastOrDefault(x => x.Sensor == s).State;
        public int Count(Sensor s) => Shown.Count(x => x.Sensor == s);
        public void Show(Sensor sensor, IconState state) => Shown.Add((sensor, state));
        public void Hide(Sensor sensor) => Hidden.Add(sensor);
    }

    private sealed class Rig
    {
        public readonly FakeClock Clock = new();
        public readonly FakeController Controller = new();
        public readonly FakeFactory Factory = new();
        public readonly FakeView View = new();
        public readonly List<string> Log = new();
        public readonly SensorLoop Loop;
        public int? Cpu = 50;
        public int? Dgpu = 0;
        public int? Chipset = 40;
        public int? CpuFan = 1800;
        public Rig()
        {
            Controller.Next = () => new ControllerSample(Cpu, Chipset, Dgpu, CpuFan, 1700);
            Loop = new SensorLoop(Controller, Factory, View, Clock, Log.Add);
        }
        public void Tick(double advance = 0)
        {
            Clock.Advance(S(advance));
            Loop.Tick();
        }
        // Two samples above zero: the card wakes (asleep polling, so 5 s apart).
        public void Wake(int c = 40)
        {
            Dgpu = c;
            Tick(5);
            Tick(5);
        }
    }

    [Fact(DisplayName = "@important the first tick shows the CPU icon with the digits and zone of the CPU value")]
    public void FirstTickCpu()
    {
        var r = new Rig { Cpu = 85 };
        r.Tick();
        var icon = r.View.Last(Sensor.Cpu)!;
        Assert.Equal("85", icon.Digits);
        Assert.Equal(HeatZone.Orange, icon.Zone);
    }

    [Fact(DisplayName = "@important a CPU value of 0 shows -- with no zone, never a green 0")]
    public void CpuZeroIsUnusable()
    {
        var r = new Rig { Cpu = 0 };
        r.Tick();
        var icon = r.View.Last(Sensor.Cpu)!;
        Assert.Equal("--", icon.Digits);
        Assert.Null(icon.Zone);
    }

    [Theory(DisplayName = "@important a CPU value of -1 or 126 shows -- with no zone, 0 and 125 are in range")]
    [InlineData(-1)]
    [InlineData(126)]
    public void CpuOutOfRangeIsUnusable(int value)
    {
        var r = new Rig { Cpu = value };
        r.Tick();
        var icon = r.View.Last(Sensor.Cpu)!;
        Assert.Equal("--", icon.Digits);
        Assert.Null(icon.Zone);
        Assert.StartsWith("CPU -- °C", icon.Tooltip);
    }

    [Fact(DisplayName = "@important a CPU value of 125 is shown, the highest accepted temperature")]
    public void CpuUpperBoundKept()
    {
        var r = new Rig { Cpu = 125 };
        r.Tick();
        Assert.Equal("125", r.View.Last(Sensor.Cpu)!.Digits);
        Assert.Equal(HeatZone.Red, r.View.Last(Sensor.Cpu)!.Zone);
    }

    [Theory(DisplayName = "@important a chipset value of -1 or 126 and a fan speed of -1 or 20001 omit their tooltip lines, 125 and 20000 stay")]
    [InlineData(-1, -1, false)]
    [InlineData(126, 20001, false)]
    [InlineData(125, 20000, true)]
    public void ChipsetAndFanRanges(int chipset, int fan, bool shown)
    {
        var r = new Rig { Chipset = chipset, CpuFan = fan };
        r.Tick();
        var tip = r.View.Last(Sensor.Cpu)!.Tooltip;
        Assert.Equal(shown, tip.Contains("Чипсет"));
        Assert.Equal(shown, tip.Contains("Вентилятор"));
    }

    [Theory(DisplayName = "@important a dGPU sample of -1 or 126 on an awake card is no sample: no hide, no read, the icon keeps its numbers")]
    [InlineData(-1)]
    [InlineData(126)]
    public void DgpuOutOfRangeWhileAwake(int value)
    {
        var r = new Rig();
        r.Wake(40);
        var reader = r.Factory.Readers[0];
        var reads = reader.Reads;
        r.Dgpu = value;
        for (var i = 0; i < 12; i++) r.Tick(2);
        Assert.Empty(r.View.Hidden);
        Assert.Equal(reads, reader.Reads);
        Assert.Equal(1, r.Factory.Opens);
        Assert.Equal("40", r.View.Last(Sensor.Dgpu)!.Digits);
    }

    [Theory(DisplayName = "@important a dGPU sample of -1 or 126 on a sleeping card never counts toward waking")]
    [InlineData(-1)]
    [InlineData(126)]
    public void DgpuOutOfRangeWhileAsleep(int value)
    {
        var r = new Rig { Dgpu = 40 };
        r.Tick();                      // one real sample above zero
        r.Dgpu = value;
        for (var i = 0; i < 4; i++) r.Tick(5);
        Assert.Equal(0, r.View.Count(Sensor.Dgpu));
        Assert.Equal(0, r.Factory.Opens);
        r.Dgpu = 40;                   // the second real sample is the one that wakes it
        r.Tick(5);
        Assert.Equal(1, r.Factory.Opens);
    }

    [Fact(DisplayName = "@important a dGPU sample of 125 is kept: the card wakes and shows 125")]
    public void DgpuUpperBoundKept()
    {
        var r = new Rig();
        r.Wake(125);
        Assert.Equal("125", r.View.Last(Sensor.Dgpu)!.Digits);
    }

    [Fact(DisplayName = "@important the dGPU icon appears and the NVIDIA stack opens exactly once, on the second sample above zero")]
    public void DgpuAppearsOnSecondSample()
    {
        var r = new Rig { Dgpu = 40 };
        r.Tick();
        Assert.Equal(0, r.View.Count(Sensor.Dgpu));
        Assert.Equal(0, r.Factory.Opens);

        r.Tick(5);
        Assert.Equal(1, r.Factory.Opens);
        var icon = r.View.Last(Sensor.Dgpu)!;
        Assert.Equal("40", icon.Digits);
        Assert.Contains("Память 52 °C", icon.Tooltip);

        r.Dgpu = 42;
        r.Tick(2);
        Assert.Equal(1, r.Factory.Opens);
        Assert.Equal("42", r.View.Last(Sensor.Dgpu)!.Digits);
    }

    [Fact(DisplayName = "@important the keeper starting with the card at 0 never calls the NVIDIA factory")]
    public void AsleepNeverOpens()
    {
        var r = new Rig { Dgpu = 0 };
        for (var i = 0; i < 6; i++) r.Tick(5);
        Assert.Equal(0, r.Factory.Opens);
        Assert.Equal(0, r.View.Count(Sensor.Dgpu));
    }

    [Fact(DisplayName = "@important while the card reports 0 the video memory reader is never read, the icon keeps the last numbers in a dip")]
    public void NoMemoryReadWhileZero()
    {
        var r = new Rig();
        r.Wake(40);
        var reader = r.Factory.Readers[0];
        var readsAtWake = reader.Reads;

        r.Dgpu = 0;
        r.Tick(2);
        r.Tick(2);
        Assert.Equal(readsAtWake, reader.Reads);
        Assert.Equal("40", r.View.Last(Sensor.Dgpu)!.Digits);
        Assert.Empty(r.View.Hidden);

        r.Dgpu = 41;
        r.Tick(30);
        Assert.Equal(readsAtWake + 1, reader.Reads);
    }

    [Fact(DisplayName = "@important ten seconds of zeros hide the dGPU icon without disposing the reader, and the next wake reuses it")]
    public void SleepKeepsReaderOpen()
    {
        var r = new Rig();
        r.Wake(40);
        Assert.Equal(1, r.Factory.Opens);

        r.Dgpu = 0;
        for (var i = 0; i < 5; i++) r.Tick(2);
        Assert.Empty(r.View.Hidden);
        r.Tick(2);
        Assert.Equal(new[] { Sensor.Dgpu }, r.View.Hidden);
        Assert.Equal(0, r.Factory.Readers[0].Disposed);

        r.Wake(38);
        Assert.Equal(1, r.Factory.Opens);
        Assert.Equal(0, r.Factory.Readers[0].Disposed);
        Assert.Equal("38", r.View.Last(Sensor.Dgpu)!.Digits);
    }

    [Fact(DisplayName = "@important a first read at a wake without values disposes that reader once and opens a second one, with one log line")]
    public void EmptyReadingReplacesReader()
    {
        var r = new Rig();
        r.Factory.Configure = (n, rd) => { if (n == 0) { rd.Value = () => null; rd.Load = () => null; } };
        r.Wake(40);
        Assert.Equal(2, r.Factory.Opens);
        Assert.Equal(1, r.Factory.Readers[0].Disposed);
        Assert.Equal(0, r.Factory.Readers[1].Disposed);
        Assert.Single(r.Log, l => l.Contains("no values"));
        Assert.DoesNotContain("Память", r.View.Last(Sensor.Dgpu)!.Tooltip);
    }

    [Fact(DisplayName = "@important a read that throws at a wake disposes that reader once and opens a second one, with one log line")]
    public void ThrowingReadReplacesReader()
    {
        var r = new Rig();
        r.Factory.Configure = (n, rd) => { if (n == 0) rd.Value = () => throw new InvalidOperationException("stale"); };
        r.Wake(40);
        Assert.Equal(2, r.Factory.Opens);
        Assert.Equal(1, r.Factory.Readers[0].Disposed);
        Assert.Single(r.Log, l => l.Contains("read failed"));
    }

    [Fact(DisplayName = "@important the controller is asked for the dGPU every tick while awake and at most every 5 seconds while asleep")]
    public void DgpuAskCadence()
    {
        var asleep = new Rig();
        for (var i = 0; i < 7; i++) asleep.Tick(i == 0 ? 0 : 2);
        Assert.Equal(new[] { true, false, false, true, false, false, true }, asleep.Controller.Asked);

        var awake = new Rig();
        awake.Wake(40);
        awake.Controller.Asked.Clear();
        for (var i = 0; i < 4; i++) awake.Tick(2);
        Assert.Equal(new[] { true, true, true, true }, awake.Controller.Asked);
    }

    [Fact(DisplayName = "@important a failed controller read shows --, resets once per failure, logs one line per series and recovers")]
    public void ControllerFailure()
    {
        var r = new Rig { Cpu = 55 };
        r.Tick();
        var ok = true;
        var inner = r.Controller.Next;
        r.Controller.Next = () => ok ? inner() : throw new InvalidOperationException("wmi gone");

        ok = false;
        for (var i = 0; i < 3; i++)
        {
            r.Tick(2);
            var icon = r.View.Last(Sensor.Cpu)!;
            Assert.Equal("--", icon.Digits);
            Assert.Null(icon.Zone);
        }
        Assert.Equal(3, r.Controller.Resets);
        Assert.Single(r.Log);

        ok = true;
        r.Tick(2);
        Assert.Equal("55", r.View.Last(Sensor.Cpu)!.Digits);
        Assert.Single(r.Log);
    }

    [Fact(DisplayName = "@important a video memory reader that throws on Open leaves the memory line out and logs once")]
    public void MemoryOpenThrows()
    {
        var r = new Rig();
        r.Factory.ThrowOnOpen = true;
        r.Wake(40);
        r.Tick(2);
        var icon = r.View.Last(Sensor.Dgpu)!;
        Assert.Equal("40", icon.Digits);
        Assert.DoesNotContain("Память", icon.Tooltip);
        Assert.Single(r.Log);
    }

    [Fact(DisplayName = "@important a video memory reader that throws on read leaves the memory line out and logs once")]
    public void MemoryReadThrows()
    {
        var r = new Rig();
        r.Wake(40);
        r.Factory.Readers[0].Value = () => throw new InvalidOperationException("nvapi");
        r.Tick(30);
        r.Tick(2);
        var icon = r.View.Last(Sensor.Dgpu)!;
        Assert.Equal("40", icon.Digits);
        Assert.DoesNotContain("Память", icon.Tooltip);
        Assert.Single(r.Log, l => l.Contains("read failed"));
        Assert.Equal(2, r.Factory.Readers[0].Reads);   // the failed read is not repeated before the next ladder step (5 s after it) is over
    }

    [Fact(DisplayName = "@important the video memory is read on a ladder: at once, then after 5 s, 10 s, 15 s ... up to every 30 s")]
    public void MemoryReadLadder()
    {
        var r = new Rig();
        r.Dgpu = 40;
        r.Tick(5);
        r.Tick(5);                           // wake: first read at once (t = 0)
        var reader = r.Factory.Readers[0];
        var reads = new List<int> { 0 };
        var seen = reader.Reads;
        for (var t = 2; t <= 140; t += 2)
        {
            r.Tick(2);
            if (reader.Reads != seen) { reads.Add(t); seen = reader.Reads; }
        }
        // 2 s ticks: waits 5, 10, 15, 20, 25, 30, 30 land on the first tick at or after each wait.
        Assert.Equal(new[] { 0, 6, 16, 32, 52, 78, 108, 138 }, reads);
        var gaps = reads.Zip(reads.Skip(1), (a, b) => b - a).ToList();
        Assert.All(gaps, g => Assert.True(g <= 30 + 2));
        Assert.Equal(gaps.OrderBy(g => g), gaps);
    }

    [Fact(DisplayName = "@important the ladder restarts at a new wake: an immediate read, then 5 s again")]
    public void MemoryLadderRestartsAtWake()
    {
        var r = new Rig();
        r.Wake(40);
        for (var i = 0; i < 40; i++) r.Tick(2);          // the ladder is at its 30 s step
        r.Dgpu = 0;
        for (var i = 0; i < 6; i++) r.Tick(2);           // asleep
        var reader = r.Factory.Readers[0];
        var before = reader.Reads;
        r.Wake(38);
        Assert.Equal(before + 1, reader.Reads);
        r.Tick(2);
        r.Tick(2);
        Assert.Equal(before + 1, reader.Reads);          // 4 s: not yet
        r.Tick(2);
        Assert.Equal(before + 2, reader.Reads);          // 6 s: the 5 s step, not the old 30 s
    }

    [Fact(DisplayName = "@important between two reads the tooltip shows the last read memory value, then the new one")]
    public void MemoryValueHeldBetweenReads()
    {
        var r = new Rig();
        r.Wake(40);
        var reader = r.Factory.Readers[0];
        reader.Value = () => 60;
        r.Tick(2);
        Assert.Contains("Память 52 °C", r.View.Last(Sensor.Dgpu)!.Tooltip);
        r.Tick(28);
        Assert.Contains("Память 60 °C", r.View.Last(Sensor.Dgpu)!.Tooltip);
    }

    [Fact(DisplayName = "@important after a sleep and a new wake the first awake sample reads the video memory at once")]
    public void MemoryReadAtOnceAfterWake()
    {
        var r = new Rig();
        r.Wake(40);
        r.Dgpu = 0;
        for (var i = 0; i < 6; i++) r.Tick(2);
        var before = r.Factory.Readers[0].Reads;
        r.Wake(38);
        Assert.Equal(before + 1, r.Factory.Readers[0].Reads);
    }

    // The tick times (seconds after the wake read, 2 s grid) at which the memory reader is read; load is a function of that time.
    private static List<int> ReadTimes(Func<int, int?> load, int until)
    {
        var t = 0;
        var r = new Rig();
        r.Factory.Load = () => load(t);
        r.Dgpu = 40;
        r.Tick(5);
        r.Tick(5);                           // wake: first read at once (t = 0)
        var reader = r.Factory.Readers[0];
        var times = new List<int> { 0 };
        var seen = reader.Reads;
        for (t = 2; t <= until; t += 2)
        {
            r.Tick(2);
            if (reader.Reads != seen) { times.Add(t); seen = reader.Reads; }
        }
        return times;
    }

    [Fact(DisplayName = "@important under a card load of 10 % or more the video memory is read every 5 s")]
    public void LoadedCardPolledEvery5Seconds()
    {
        Assert.Equal(new[] { 0, 6, 12, 18, 24, 30 }, ReadTimes(_ => 40, 30));
        Assert.Equal(new[] { 0, 6, 12, 18 }, ReadTimes(_ => 10, 18));      // 10 is active
        Assert.Equal(new[] { 0, 6, 16, 32 }, ReadTimes(_ => 9, 32));       // 9 is idle: the ladder grows
    }

    [Fact(DisplayName = "@important a null load counts as idle")]
    public void NullLoadIsIdle()
    {
        Assert.Equal(new[] { 0, 6, 16, 32 }, ReadTimes(_ => null, 32));
    }

    [Fact(DisplayName = "@important when the load falls the waits grow from 5 s, when it rises again the wait returns to 5 s")]
    public void LoadFallsAndRises()
    {
        // loaded until 17 s, idle from 18 s to 43 s, loaded again from 44 s
        var times = ReadTimes(t => t < 18 || t >= 44 ? 40 : 0, 56);
        Assert.Equal(new[] { 0, 6, 12, 18, 28, 44, 50, 56 }, times);
    }
    [Fact(DisplayName = "@important a chipset value of 0 leaves the chipset line out, a fan speed of 0 stays")]
    public void ChipsetZeroOmitted()
    {
        var r = new Rig { Chipset = 0, CpuFan = 0 };
        r.Tick();
        var tip = r.View.Last(Sensor.Cpu)!.Tooltip;
        Assert.DoesNotContain("Чипсет", tip);
        Assert.Contains("Вентилятор", tip);
    }

    [Fact(DisplayName = "@important a sampling step stuck for 20 s puts both icons in the failure look once; the next good step restores the numbers")]
    public void StuckStepShowsDashes()
    {
        var r = new Rig();
        r.Wake();
        var calls = 0;
        r.Controller.Next = () =>
        {
            if (++calls == 1)   // the hanging call: the clock runs on while the sampling thread sits in it, the watchdog looks twice
            {
                r.Clock.Advance(S(25));
                r.Loop.CheckStuck();
                r.Loop.CheckStuck();
            }
            return new ControllerSample(r.Cpu, r.Chipset, r.Dgpu, r.CpuFan, 1700);
        };
        r.Tick(5);
        Assert.Equal(1, r.Log.Count(l => l.Contains("stuck")));
        var cpuIcons = r.View.Shown.Where(x => x.Sensor == Sensor.Cpu).Select(x => x.State.Digits).TakeLast(2).ToArray();
        Assert.Equal(["--", "50"], cpuIcons);
        Assert.Equal("40", r.View.Last(Sensor.Dgpu)!.Digits);
    }

    [Fact(DisplayName = "@important a controller failure of 30 s with the card awake hides the dGPU icon; a good read brings it back")]
    public void LongFailureHidesDgpuIcon()
    {
        var r = new Rig();
        r.Wake();
        r.Controller.Next = () => throw new InvalidOperationException("controller gone");
        r.Tick(2);
        Assert.Equal("--", r.View.Last(Sensor.Dgpu)!.Digits);
        Assert.Empty(r.View.Hidden);
        r.Tick(31);
        Assert.Single(r.View.Hidden, Sensor.Dgpu);
        r.Tick(2);
        Assert.Single(r.View.Hidden, Sensor.Dgpu);   // hidden once, not on every failed step
        r.Controller.Next = () => new ControllerSample(r.Cpu, r.Chipset, r.Dgpu, r.CpuFan, 1700);
        r.Tick(2);
        Assert.Equal("40", r.View.Last(Sensor.Dgpu)!.Digits);
    }

    [Fact(DisplayName = "@critical ending the keeper closes the NVIDIA stack only while the card is awake: closing it wakes a sleeping card")]
    public async Task ShutdownKeepsReaderOpenWhileCardSleeps()
    {
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var awake = new Rig();
        awake.Wake();
        awake.Tick(0);
        Assert.Single(awake.Factory.Readers);
        await awake.Loop.RunAsync(cancelled.Token);
        Assert.Equal(1, awake.Factory.Readers[0].Disposed);

        var asleep = new Rig();
        asleep.Wake();
        asleep.Dgpu = 0;
        asleep.Tick(5);
        asleep.Tick(11);
        Assert.Single(asleep.Factory.Readers);
        Assert.Equal(0, asleep.Factory.Readers[0].Disposed);   // kept open while the card sleeps
        await asleep.Loop.RunAsync(cancelled.Token);
        Assert.Equal(0, asleep.Factory.Readers[0].Disposed);
    }

    [Fact(DisplayName = "@important while the plugin watches, the awake card is read on every step and the latest state follows; a sleeping card is never asked")]
    public void WatchReadsEveryStepAndPublishesTheState()
    {
        var r = new Rig();
        r.Factory.Load = () => 37;
        r.Wake();
        r.Tick(2); r.Tick(2); r.Tick(2);
        var before = r.Factory.Readers.Sum(x => x.Reads);
        r.Loop.Watch();
        r.Tick(2); r.Tick(2); r.Tick(2); r.Tick(2);
        Assert.Equal(before + 4, r.Factory.Readers.Sum(x => x.Reads));
        var live = r.Loop.Latest;
        Assert.True(live.Awake);
        Assert.Equal(40, live.TempC);
        Assert.Equal(37, live.LoadPercent);

        r.Clock.Advance(S(20));   // the watch ran out: the ladder again
        var after = r.Factory.Readers.Sum(x => x.Reads);
        r.Tick(2);
        Assert.True(r.Factory.Readers.Sum(x => x.Reads) - after <= 1);

        r.Dgpu = 0;               // the card goes to sleep: nothing is asked of it, whatever the watch says
        for (var i = 0; i < 8; i++) r.Tick(2);
        r.Loop.Watch();
        var asleep = r.Factory.Readers.Sum(x => x.Reads);
        r.Tick(2); r.Tick(2);
        Assert.Equal(asleep, r.Factory.Readers.Sum(x => x.Reads));
        Assert.False(r.Loop.Latest.Awake);
    }

    [Fact(DisplayName = "@critical a card being ejected is let go of: the NVIDIA library is dropped at once and not opened again until the release runs out or the card has slept")]
    public void ReleaseCardDropsTheLibrary()
    {
        var r = new Rig();
        r.Wake();
        r.Tick(2);
        Assert.Single(r.Factory.Readers);
        var disposed = r.Factory.Readers[0].Disposed;

        r.Loop.ReleaseCard(S(60));
        Assert.Equal(disposed + 1, r.Factory.Readers[0].Disposed);   // dropped now, not at the next step
        for (var i = 0; i < 20; i++) r.Tick(2);                      // 40 s, the card still reports awake
        Assert.Single(r.Factory.Readers);                            // never opened again
        Assert.Equal(40, r.Loop.Latest.TempC);                       // the icon keeps its temperature

        for (var i = 0; i < 12; i++) r.Tick(2);                      // the release (60 s) has run out
        Assert.Equal(2, r.Factory.Readers.Count);

        r.Loop.ReleaseCard(S(300));
        r.Dgpu = 0;                                                  // the card went away: the release ends with it
        for (var i = 0; i < 8; i++) r.Tick(2);
        r.Dgpu = 40;
        r.Tick(5); r.Tick(5);
        r.Tick(2);
        Assert.True(r.Factory.Readers.Count >= 3);                   // a woken card is read again
    }
}
