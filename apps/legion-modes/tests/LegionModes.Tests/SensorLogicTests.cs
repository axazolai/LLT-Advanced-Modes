using LegionModes.Core.Sensors;
using Xunit;

namespace LegionModes.Tests;

public class SensorLogicTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact(DisplayName = "@important CPU heat zones change at 60, 80 and 90")]
    public void CpuZones()
    {
        Assert.Equal(HeatZone.Green, Heat.For(Sensor.Cpu, 59));
        Assert.Equal(HeatZone.Yellow, Heat.For(Sensor.Cpu, 60));
        Assert.Equal(HeatZone.Yellow, Heat.For(Sensor.Cpu, 79));
        Assert.Equal(HeatZone.Orange, Heat.For(Sensor.Cpu, 80));
        Assert.Equal(HeatZone.Orange, Heat.For(Sensor.Cpu, 89));
        Assert.Equal(HeatZone.Red, Heat.For(Sensor.Cpu, 90));
    }

    [Fact(DisplayName = "@important dGPU heat zones change at 60, 70 and 75")]
    public void DgpuZones()
    {
        Assert.Equal(HeatZone.Green, Heat.For(Sensor.Dgpu, 59));
        Assert.Equal(HeatZone.Yellow, Heat.For(Sensor.Dgpu, 60));
        Assert.Equal(HeatZone.Yellow, Heat.For(Sensor.Dgpu, 69));
        Assert.Equal(HeatZone.Orange, Heat.For(Sensor.Dgpu, 70));
        Assert.Equal(HeatZone.Orange, Heat.For(Sensor.Dgpu, 74));
        Assert.Equal(HeatZone.Red, Heat.For(Sensor.Dgpu, 75));
    }

    [Fact(DisplayName = "@important dGPU is awake at the second sample above zero and not before")]
    public void PresenceNeedsTwoSamples()
    {
        var p = new DgpuPresence(new FakeClock());
        Assert.False(p.Update(0));
        Assert.False(p.Update(5));
        Assert.False(p.Awake);
        Assert.True(p.Update(5));
        Assert.True(p.Awake);
    }

    [Fact(DisplayName = "@important dGPU sleeps only after ten seconds of zeros on end; a sample above zero restarts the count")]
    public void PresenceTenSecondRule()
    {
        var clock = new FakeClock();
        var p = new DgpuPresence(clock);
        p.Update(40);
        p.Update(40);
        Assert.True(p.Awake);

        p.Update(0);
        clock.Advance(S(9));
        Assert.True(p.Update(0));

        p.Update(40);                       // restarts the count
        clock.Advance(S(2));
        p.Update(0);
        clock.Advance(S(9));
        Assert.True(p.Update(0));           // nine seconds since the first zero of this run

        clock.Advance(S(1));
        Assert.False(p.Update(0));          // ten seconds
        Assert.False(p.Awake);
    }

    [Fact(DisplayName = "@important a null dGPU sample changes nothing in the presence")]
    public void PresenceNullSample()
    {
        var clock = new FakeClock();
        var p = new DgpuPresence(clock);
        p.Update(5);
        p.Update(null);
        Assert.False(p.Awake);
        Assert.True(p.Update(5));           // the null did not reset the count
        p.Update(0);
        clock.Advance(S(30));
        Assert.True(p.Update(null));        // nor does it let the card fall asleep
    }

    [Fact(DisplayName = "@important peak keeps the first-minute maximum within the day and drops it after 25 hours")]
    public void PeakWindow()
    {
        var clock = new FakeClock();
        var peak = new PeakTracker(clock);
        Assert.Null(peak.Peak);
        peak.Add(91);
        for (var i = 0; i < 90; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            peak.Add(30);
        }
        Assert.Equal(91, peak.Peak);
        Assert.False(peak.OverDay);

        clock.Advance(TimeSpan.FromHours(25) - TimeSpan.FromMinutes(90));
        peak.Add(40);
        Assert.Equal(40, peak.Peak);
        Assert.True(peak.OverDay);
    }

    [Fact(DisplayName = "@important peak survives the clock moving back one hour")]
    public void PeakClockBack()
    {
        var clock = new FakeClock();
        var peak = new PeakTracker(clock);
        peak.Add(60);
        clock.Advance(TimeSpan.FromMinutes(5));
        peak.Add(80);
        clock.Advance(TimeSpan.FromHours(-1));
        peak.Add(70);
        Assert.Equal(80, peak.Peak);
    }

    [Fact(DisplayName = "@important CPU tooltip lists CPU, chipset, fan and the session peak, then the 24-hour peak over a day")]
    public void CpuTooltip()
    {
        var s = new ControllerSample(49, 46, null, 1800, null);
        Assert.Equal("CPU 49 °C\nЧипсет 46 °C\nВентилятор 1800 об/мин\nПик с начала сеанса: 91 °C", TooltipText.Cpu(s, 91, false));
        Assert.EndsWith("\nПик за последние 24 часа: 91 °C", TooltipText.Cpu(s, 91, true));
    }

    [Fact(DisplayName = "@important a null chipset or fan omits its tooltip line")]
    public void NullLinesOmitted()
    {
        var s = new ControllerSample(49, null, 38, null, null);
        Assert.Equal("CPU 49 °C\nПик с начала сеанса: 91 °C", TooltipText.Cpu(s, 91, false));
        Assert.Equal("dGPU 38 °C\nПик с начала сеанса: 40 °C", TooltipText.Dgpu(s, null, 40, false));
    }

    [Fact(DisplayName = "@important dGPU tooltip shows the memory line only when a memory temperature was read")]
    public void DgpuTooltipMemory()
    {
        var s = new ControllerSample(49, 46, 43, 1800, 1700);
        Assert.Equal("dGPU 43 °C\nПамять 52 °C\nВентилятор 1700 об/мин\nПик с начала сеанса: 60 °C", TooltipText.Dgpu(s, 52, 60, false));
        Assert.DoesNotContain("Память", TooltipText.Dgpu(s, null, 60, false));
    }

    [Fact(DisplayName = "@important every tooltip stays within 127 characters")]
    public void TooltipLength()
    {
        var s = new ControllerSample(125, 125, 125, 99999, 99999);
        Assert.True(TooltipText.Cpu(s, 125, true).Length <= 127);
        Assert.True(TooltipText.Dgpu(s, 125, 125, true).Length <= 127);
        var s2 = new ControllerSample(49, 46, 43, 1800, 1700);
        Assert.True(TooltipText.Cpu(s2, 91, true).Length <= 127);
        Assert.True(TooltipText.Dgpu(s2, 52, 91, true).Length <= 127);
    }

    [Fact(DisplayName = "@important a peak recorded while the clock ran ahead ages out 24 hours after the clock came back")]
    public void PeakFromFutureClockAgesOut()
    {
        var clock = new FakeClock();
        var peak = new PeakTracker(clock);
        peak.Add(60);
        clock.Advance(TimeSpan.FromDays(400));   // the wrong clock
        peak.Add(95);
        clock.Advance(TimeSpan.FromDays(-400));  // NTP corrects it
        Assert.Equal(95, peak.Peak);
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Null(peak.Peak);
    }
}
