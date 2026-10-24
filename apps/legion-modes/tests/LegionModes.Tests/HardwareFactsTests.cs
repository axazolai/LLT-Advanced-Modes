using LegionModes.Core.Model;
using LegionModes.Core.Status;
using Xunit;

namespace LegionModes.Tests;

public class HardwareFactsTests
{
    [Fact(DisplayName = "@important a fixed panel rate matches only a true signal at that rate without DRR (1 Hz of slack); DRR matches only the DRR flag; do not touch always matches")]
    public void PanelMatching()
    {
        var fixed60 = new PanelRate(PanelRateKind.Fixed, 60);
        Assert.True(HardwareFacts.PanelMatches(fixed60, 60, false));
        Assert.True(HardwareFacts.PanelMatches(fixed60, 59, false));
        Assert.False(HardwareFacts.PanelMatches(fixed60, 240, false));   // 60 Hz in the system but a 240 Hz signal is the 1.1 W loss of R-1610
        Assert.False(HardwareFacts.PanelMatches(fixed60, 60, true));
        var drr = new PanelRate(PanelRateKind.Drr);
        Assert.True(HardwareFacts.PanelMatches(drr, 120, true));
        Assert.False(HardwareFacts.PanelMatches(drr, 120, false));
        Assert.True(HardwareFacts.PanelMatches(new PanelRate(PanelRateKind.DoNotTouch), 33, true));
    }

    [Fact(DisplayName = "@important the charging mode in the driver matches only the profile's own mode; do not touch matches anything")]
    public void ChargeMatching()
    {
        Assert.True(HardwareFacts.ChargeMatches(ChargingMode.Conservation, ChargeKind.Conservation));
        Assert.False(HardwareFacts.ChargeMatches(ChargingMode.Conservation, ChargeKind.Normal));
        Assert.False(HardwareFacts.ChargeMatches(ChargingMode.Rapid, ChargeKind.Normal));
        Assert.True(HardwareFacts.ChargeMatches(ChargingMode.Normal, ChargeKind.Normal));
        Assert.True(HardwareFacts.ChargeMatches(ChargingMode.DoNotTouch, ChargeKind.Rapid));
    }

    [Fact(DisplayName = "@important processor facts: boost index 2 is Enabled and 0 Disabled, EPP exact, the overlay compared; an unreadable or untouched field never counts as a difference")]
    public void ProcessorMatching()
    {
        var asked = new ProcessorScheme(BoostMode.Disabled, 100, WindowsPowerMode.BestPowerEfficiency);
        Assert.True(HardwareFacts.ProcessorMatches(asked, 0, 100, WindowsPowerMode.BestPowerEfficiency));
        Assert.False(HardwareFacts.ProcessorMatches(asked, 2, 100, WindowsPowerMode.BestPowerEfficiency));
        Assert.False(HardwareFacts.ProcessorMatches(asked, 0, 80, WindowsPowerMode.BestPowerEfficiency));
        Assert.False(HardwareFacts.ProcessorMatches(asked, 0, 100, WindowsPowerMode.Balanced));
        Assert.True(HardwareFacts.ProcessorMatches(asked, null, null, null));
        Assert.True(HardwareFacts.ProcessorMatches(new ProcessorScheme(BoostMode.DoNotTouch, null, WindowsPowerMode.DoNotTouch), 2, 7, WindowsPowerMode.Balanced));
        Assert.True(HardwareFacts.ProcessorMatches(new ProcessorScheme(BoostMode.Enabled, null, WindowsPowerMode.DoNotTouch), 2, 7, null));
    }

    [Fact(DisplayName = "@important the USB and the projection of the hardware match the profile's own value; do not touch matches any projection")]
    public void UsbAndProjection()
    {
        Assert.True(HardwareFacts.UsbMatches(UsbPower.Always, UsbKind.Always));
        Assert.False(HardwareFacts.UsbMatches(UsbPower.Off, UsbKind.WhenSleeping));
        Assert.True(HardwareFacts.ProjectionMatches(Projection.DoNotTouch, Projection.Duplicate));
        Assert.True(HardwareFacts.ProjectionMatches(Projection.Extend, Projection.Extend));
        Assert.False(HardwareFacts.ProjectionMatches(Projection.ExternalOnly, Projection.Extend));
    }

    [Fact(DisplayName = "@important timers read back from a scheme turn into the profile's terms and match the profile's own values; the dimmed brightness counts only with a dim stage; lighting 0 (idle) is no difference")]
    public void TimersAndLighting()
    {
        var asked = LegionModes.Core.Engine.WindowsTimers.From(new IdleTimers(new DimStage(10, 20), 15, 60, 0, null));
        Assert.True(HardwareFacts.TimersMatch(asked, asked with { StaticBrightness = null }));
        Assert.False(HardwareFacts.TimersMatch(asked, asked with { VideoIdleSec = 600 }));
        Assert.False(HardwareFacts.TimersMatch(asked, asked with { DimmedBrightness = 50 }));
        var noDim = LegionModes.Core.Engine.WindowsTimers.From(new IdleTimers(null, 5, 10, 60, null));
        Assert.True(HardwareFacts.TimersMatch(noDim, noDim with { DimmedBrightness = 1 }));

        var back = LegionModes.Core.Engine.WindowsTimers.ToIdle(LegionModes.Core.Engine.WindowsTimers.From(new IdleTimers(new DimStage(10, 20), 15, 60, 30, null)));
        Assert.Equal(new IdleTimers(new DimStage(10, 20), 15, 60, 30, null), back);   // hibernate counts from falling asleep in the profile, from idle in the scheme
        Assert.Equal(0, LegionModes.Core.Engine.WindowsTimers.ToIdle(LegionModes.Core.Engine.WindowsTimers.From(new IdleTimers(null, 5, 10, 0, null))).HibernateMin);

        var light = new Lighting(3, 3);
        Assert.True(HardwareFacts.LightingMatches(light, 3, 3));
        Assert.True(HardwareFacts.LightingMatches(light, 3, 0));    // the idle lighting holds it off
        Assert.False(HardwareFacts.LightingMatches(light, 2, 3));
        Assert.False(HardwareFacts.LightingMatches(light, 3, 7));
        Assert.False(HardwareFacts.LightingMatches(new Lighting(3, 0), 3, 4));
    }
}
