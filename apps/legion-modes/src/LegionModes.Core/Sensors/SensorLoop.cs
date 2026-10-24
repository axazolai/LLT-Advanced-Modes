using System.Globalization;
using LegionModes.Core.Engine;

namespace LegionModes.Core.Sensors;

// One sampling step per Tick: reads the controller (values outside their ranges become null, see Readings), keeps the peaks, shows
// or hides the two icons. The NVIDIA stack is opened only
// after the dGPU has been awake for two samples, asked only in a sample where the controller reports the card above zero, and
// kept open while the card sleeps (closing it wakes the card); a read without values replaces it. Never throws.
public sealed class SensorLoop
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan AsleepPoll = TimeSpan.FromSeconds(5);
    // A controller failure that lasts this long hides the dGPU icon; a sampling step that runs this long (a hanging WMI call) shows "--".
    static readonly TimeSpan FailureHidesDgpu = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(20);
    // From this core load on the card is in use and the video memory is read every 5 s; below it the wait grows by 5 s per read, up to 30 s.
    public const int ActiveLoadPercent = 10;
    static readonly TimeSpan MemoryStep = TimeSpan.FromSeconds(5);
    static readonly TimeSpan MemoryMax = TimeSpan.FromSeconds(30);
    static readonly ControllerSample Empty = new(null, null, null, null, null);

    readonly IControllerReader _controller;
    readonly IMemoryTempReaderFactory _memory;
    readonly ITrayView _view;
    readonly IClock _clock;
    readonly Action<string> _log;
    readonly DgpuPresence _presence;
    readonly PeakTracker _cpuPeak;
    readonly PeakTracker _dgpuPeak;
    IMemoryTempReader? _reader;
    DateTimeOffset _lastDgpuAsk = DateTimeOffset.MinValue;
    bool _controllerFailing;
    DateTimeOffset? _failingSince;                  // the first failed controller read of the current series
    bool _dgpuHiddenByFailure;
    long _tickStartTicks;                           // the clock reading when the running Tick began (0 = none running)
    bool _stuckShown;
    bool _memoryFailing;
    IconState? _lastDgpu;
    DateTimeOffset _lastMemoryRead = DateTimeOffset.MinValue;
    int? _lastMemory;
    int? _lastLoad;
    GpuLive _latest = new(false, null, null, null);
    long _watchUntilTicks;
    long _releaseUntilTicks;
    TimeSpan _memoryWait = TimeSpan.Zero;   // the wait before the next video memory read: 0 on a wake, +5 s per read, at most 30 s

    public SensorLoop(IControllerReader controller, IMemoryTempReaderFactory memory, ITrayView view, IClock clock, Action<string> log)
    {
        _controller = controller;
        _memory = memory;
        _view = view;
        _clock = clock;
        _log = log;
        _presence = new DgpuPresence(clock);
        _cpuPeak = new PeakTracker(clock);
        _dgpuPeak = new PeakTracker(clock);
    }

    // The card's state as of the last step (read from any thread).
    public GpuLive Latest => Volatile.Read(ref _latest);

    // The plugin's Status tab is on screen: for the next WatchFor the NVIDIA stack is asked every step while the card is awake (the way LLT's own
    // GPU block polls while it is shown), then the ladder returns. A sleeping card is never asked.
    static readonly TimeSpan WatchFor = TimeSpan.FromSeconds(10);
    // The plugin is ejecting the card (iGPU-only): an open NVIDIA library keeps Windows from letting it go, so the library is dropped and not
    // opened again for this long, or until the card has gone to sleep or away.
    public void ReleaseCard(TimeSpan forHow)
    {
        Volatile.Write(ref _releaseUntilTicks, (_clock.Now + forHow).UtcTicks);
        if (_reader is not null) { _log("video memory reader dropped: the card is being ejected"); DropReader(); }
    }

    bool Released => _clock.Now.UtcTicks < Volatile.Read(ref _releaseUntilTicks);

    // Dropped without closing: closing the library wakes a sleeping card; the helper process is simply ended.
    void DropReader()
    {
        var reader = _reader;
        _reader = null;
        try { reader?.Dispose(); }
        catch (Exception e) { _log("video memory reader drop failed: " + e.Message); }
    }

    public void Watch() => Volatile.Write(ref _watchUntilTicks, (_clock.Now + WatchFor).UtcTicks);

    public async Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>   // the watchdog: the sampling thread may sit in a hanging call, so it is watched from outside
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { break; }
                CheckStuck();
            }
        }, CancellationToken.None);
        while (!ct.IsCancellationRequested)
        {
            Tick();
            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
        Shutdown();
    }

    public void Tick()
    {
        Volatile.Write(ref _tickStartTicks, _clock.Now.UtcTicks);
        try { Step(); }
        catch (Exception e) { Note(ref _controllerFailing, "sensor step failed: " + e.Message); }
        finally
        {
            Volatile.Write(ref _tickStartTicks, 0);
            _stuckShown = false;
        }
    }

    // Called from a second thread: a sampling step running longer than StuckAfter puts both icons in the failure look, once.
    public void CheckStuck()
    {
        try
        {
            var started = Volatile.Read(ref _tickStartTicks);
            if (started == 0 || _stuckShown || _clock.Now.UtcTicks - started < StuckAfter.Ticks) return;
            _stuckShown = true;
            _log($"sensor step stuck for {StuckAfter.TotalSeconds:0} s: icons show --");
            _view.Show(Sensor.Cpu, new IconState("--", null, TooltipText.Cpu(Empty, _cpuPeak.Peak, _cpuPeak.OverDay)));
            if (_presence.Awake) _view.Show(Sensor.Dgpu, new IconState("--", null, TooltipText.Dgpu(Empty, null, _dgpuPeak.Peak, _dgpuPeak.OverDay)));
        }
        catch (Exception e) { _log("sensor watchdog failed: " + e.Message); }
    }

    void Step()
    {
        var askDgpu = _presence.Awake || _clock.Now - _lastDgpuAsk >= AsleepPoll;
        ControllerSample s;
        try
        {
            s = _controller.Read(askDgpu);
            s = Readings.Normalize(s);
            if (askDgpu) _lastDgpuAsk = _clock.Now;
        }
        catch (Exception e)
        {
            Note(ref _controllerFailing, "controller read failed: " + e.Message);
            try { _controller.Reset(); } catch { /* the next read starts afresh anyway */ }
            _failingSince ??= _clock.Now;
            _view.Show(Sensor.Cpu, new IconState("--", null, TooltipText.Cpu(Empty, _cpuPeak.Peak, _cpuPeak.OverDay)));
            if (_presence.Awake)
            {
                if (_clock.Now - _failingSince >= FailureHidesDgpu)   // a failure that lasts: the grey icon goes away until the reads work again
                {
                    if (!_dgpuHiddenByFailure) { _dgpuHiddenByFailure = true; _lastDgpu = null; _view.Hide(Sensor.Dgpu); }
                }
                else _view.Show(Sensor.Dgpu, new IconState("--", null, TooltipText.Dgpu(Empty, null, _dgpuPeak.Peak, _dgpuPeak.OverDay)));
            }
            return;
        }
        _controllerFailing = false;
        _failingSince = null;
        _dgpuHiddenByFailure = false;

        // CPU: a real CPU never reads 0, so 0 (a stuck controller) is shown as unusable.
        var cpu = s.CpuC is > 0 ? s.CpuC : null;
        if (cpu is { } c) _cpuPeak.Add(c);
        var cpuTip = TooltipText.Cpu(s, _cpuPeak.Peak, _cpuPeak.OverDay);
        _view.Show(Sensor.Cpu, cpu is { } v
            ? new IconState(v.ToString(CultureInfo.InvariantCulture), Heat.For(Sensor.Cpu, v), cpuTip)
            : new IconState("--", null, cpuTip));

        // dGPU
        var wasAwake = _presence.Awake;
        var awake = _presence.Update(askDgpu ? s.DgpuC : null);
        if (awake && !wasAwake) ResetLadder();
        if (!awake && wasAwake)
        {
            Volatile.Write(ref _releaseUntilTicks, 0);
            ResetLadder();
            _lastDgpu = null;
            _view.Hide(Sensor.Dgpu);
        }
        if (!awake) { Volatile.Write(ref _latest, new GpuLive(false, null, null, null)); return; }
        if (s.DgpuC is > 0)   // the card is up in this very sample: only then may the NVIDIA stack be asked
        {
            var g = s.DgpuC.Value;
            _dgpuPeak.Add(g);
            if (!Released && (_clock.Now.UtcTicks < Volatile.Read(ref _watchUntilTicks) || _clock.Now - _lastMemoryRead >= _memoryWait))   // the ladder: an access keeps the card awake for about 15 s, so waits above that let an unused card sleep
            {
                if (_reader is null) OpenMemory();
                var reading = ReadMemory();
                if (reading is { } r && (r.MemoryC is not null || r.LoadPercent is not null)) _memoryFailing = false;
                else if (_reader is not null)   // no values or a throw: replace the reader; the card is awake in this sample, so closing it is harmless
                {
                    if (reading is not null) Note(ref _memoryFailing, "video memory reader gave no values");
                    DisposeReader();
                    OpenMemory();
                }
                _lastMemory = reading?.MemoryC;
                _lastLoad = reading?.LoadPercent;
                _lastMemoryRead = _clock.Now;
                _memoryWait = reading?.LoadPercent >= ActiveLoadPercent
                    ? MemoryStep
                    : TimeSpan.FromTicks(Math.Min((_memoryWait + MemoryStep).Ticks, MemoryMax.Ticks));
            }
            _lastDgpu = new IconState(g.ToString(CultureInfo.InvariantCulture), Heat.For(Sensor.Dgpu, g),
                TooltipText.Dgpu(s, _lastMemory, _dgpuPeak.Peak, _dgpuPeak.OverDay));
        }
        Volatile.Write(ref _latest, new GpuLive(true, s.DgpuC is > 0 ? s.DgpuC : null, _lastMemory, _lastLoad));
        if (_lastDgpu is { } state) _view.Show(Sensor.Dgpu, state);   // a short dip to 0 keeps the last numbers
    }

    // The first read after a wake follows at once.
    void ResetLadder()
    {
        _lastMemoryRead = DateTimeOffset.MinValue;
        _lastMemory = null;
        _lastLoad = null;
        _memoryWait = TimeSpan.Zero;
    }

    void OpenMemory()
    {
        try
        {
            _reader = _memory.Open();
            _log("video memory reader opened");
        }
        catch (Exception e)
        {
            _reader = null;
            Note(ref _memoryFailing, "video memory reader not available: " + e.Message);
        }
    }

    GpuReading? ReadMemory()
    {
        if (_reader is null) return null;
        try { return _reader.Read(); }
        catch (Exception e)
        {
            Note(ref _memoryFailing, "video memory read failed: " + e.Message);
            return null;
        }
    }

    void DisposeReader()
    {
        var reader = _reader;
        _reader = null;
        if (reader is null) return;
        try { reader.Dispose(); }
        catch (Exception e) { _log("video memory reader close failed: " + e.Message); }
        _log("video memory reader closed");
    }

    // One log line per failure series: the flag is cleared by the next good step.
    void Note(ref bool failing, string text)
    {
        if (failing) return;
        failing = true;
        _log(text);
    }

    void Shutdown()
    {
        if (_presence.Awake) DisposeReader();
        else if (_reader is not null)   // closing the NVIDIA stack wakes a sleeping card: the process end drops it instead
        {
            _reader = null;
            _log("video memory reader left open: the card sleeps");
        }
        try { _view.Hide(Sensor.Cpu); _view.Hide(Sensor.Dgpu); } catch { /* the keeper is ending */ }
    }
}
