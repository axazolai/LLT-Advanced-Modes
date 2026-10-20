using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Core.Status;

namespace LegionModes.Tests;

public sealed class RetryAndBurstTests : IDisposable
{
    static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "LegionModes.Retry." + Guid.NewGuid().ToString("N"));

    public RetryAndBurstTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact(DisplayName = "@important a failed parameter is retried 3 times, 60 s apart, for the same value; then it is left until the value changes")]
    public void RetriesAreBounded()
    {
        var retry = new RetryTracker(Minute);
        object value = "a";
        object Current(ParameterId _) => value;
        var now = T0;
        retry.Record(ParameterId.Panel, value, failed: true, now);

        Assert.Empty(retry.Due(now + Minute - TimeSpan.FromSeconds(1), Current));
        for (var i = 1; i <= 3; i++)
        {
            now += Minute;
            Assert.Equal([ParameterId.Panel], retry.Due(now, Current));            // retry i
            retry.Record(ParameterId.Panel, value, failed: true, now);
        }
        Assert.Empty(retry.Due(now + Minute * 10, Current));                          // 3 retries are used

        value = "b";                                                                   // a new value is applied by the normal diff
        Assert.Empty(retry.Due(now + Minute * 11, Current));
        retry.Record(ParameterId.Panel, value, failed: true, now + Minute * 11);
        Assert.Equal([ParameterId.Panel], retry.Due(now + Minute * 12, Current));      // and starts its own 3 retries
    }

    [Fact(DisplayName = "@important a parameter that succeeds leaves the retry list")]
    public void SuccessClears()
    {
        var retry = new RetryTracker(Minute);
        retry.Record(ParameterId.Gpu, "x", failed: true, T0);
        retry.Record(ParameterId.Gpu, "x", failed: false, T0 + Minute);
        Assert.Empty(retry.Due(T0 + Minute * 5, _ => "x"));
    }

    [Fact(DisplayName = "@important a DRR fallback is a note, not a failure; a read-back mismatch is an error that is retried")]
    public void ResultKinds()
    {
        Assert.False(ParameterResult.Note("DRR недоступен, стоит 120 Гц").Failed);
        Assert.True(ParameterResult.Of("панель: сигнал 60 Гц, ожидался 120 Гц").Failed);
        Assert.True(ParameterResult.Fault("MissingMethodException: x").Failed);
        Assert.False(ParameterResult.Of(null).Failed);
    }

    static Profile P(string name) => FactoryProfiles.Create().Profiles.Single(p => p.Name == name);

    [Fact(DisplayName = "@important profile changes of a burst chain into one transition; a burst that ends where it began has none")]
    public void BurstTransitions()
    {
        Profile a = P("Сеть, открыт, простой"), b = P("Батарея, открыт, простой"), c = P("Слабый адаптер, открыт, простой");

        Assert.Null(Transition.Chain(new Transition(a, b), new Transition(b, a)));
        var chained = Transition.Chain(new Transition(a, b), new Transition(b, c))!;
        Assert.Equal(a.Id, chained.From!.Id);
        Assert.Equal(c.Id, chained.To.Id);
        Assert.Equal(b.Id, Transition.Chain(null, new Transition(a, b))!.To.Id);
        Assert.Equal(b.Id, Transition.Chain(new Transition(a, b), null)!.To.Id);
        Assert.Null(Transition.Chain(null, null));
    }

    [Fact(DisplayName = "@critical every source change reaches the engine: holds and overrides lift even when the burst returns to the start")]
    public void EachChangeLiftsOverrides()
    {
        var clock = new FakeClock();
        var engine = new ProfileEngine(FactoryProfiles.Create(), clock);
        var ac = new Inputs(PowerSource.Ac, LidState.Open, Workload.Idle, false);
        engine.Update(ac);
        engine.Set(OverrideKind.StayAwake);

        // the runtime feeds each raw change as it arrives: Ac -> Battery -> Ac
        engine.Update(ac with { Source = PowerSource.Battery });
        var back = engine.Update(ac);

        Assert.DoesNotContain(back.Overrides, o => o.Kind == OverrideKind.StayAwake);
    }

    [Fact(DisplayName = "@critical an unreadable control.json is told apart from a missing one, and neither becomes a record to write over")]
    public void ControlFileMissingAndUnreadable()
    {
        var path = Path.Combine(_dir, "control.json");
        var missing = ControlFile.Read(path);
        Assert.True(missing.Missing);
        Assert.Null(missing.Error);
        Assert.Equal(ControlData.Empty.HasControl, missing.Data.HasControl);

        ControlFile.Save(path, ControlData.Empty with { HasControl = true, SavedLidActionAc = 1, SavedLidActionDc = 2, Running = true });
        var read = ControlFile.Read(path);
        Assert.Null(read.Error);
        Assert.True(read.Data.HasControl);
        Assert.Equal(1u, read.Data.SavedLidActionAc);

        File.WriteAllText(path, "{ \"hasControl\": tr");                        // a truncated file
        var unreadable = ControlFile.Read(path);
        Assert.False(unreadable.Missing);
        Assert.NotNull(unreadable.Error);

        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);   // a sharing violation
        var locked = ControlFile.Read(path);
        Assert.NotNull(locked.Error);
        Assert.False(locked.Missing);
    }
}
