using LegionModes.Core.Model;
using LegionModes.Core.Status;
using Xunit;

namespace LegionModes.Tests;

public class FirmwareFactsTests
{
    [Theory(DisplayName = "@important the firmware's GPU mode codes read as Hybrid, integrated only and Hybrid auto; the G-Sync flag means discrete only whatever the iGPU code says")]
    [InlineData(0, 0, false, GpuMode.Hybrid)]
    [InlineData(0, 1, false, GpuMode.HybridIgpuOnly)]
    [InlineData(0, 2, false, GpuMode.HybridAuto)]
    [InlineData(1, 1, true, null)]
    [InlineData(1, 0, true, null)]
    public void GpuCodes(int gsync, int igpu, bool discrete, GpuMode? mode)
    {
        var actual = FirmwareFacts.Gpu(gsync, igpu)!;
        Assert.Equal(discrete, actual.DiscreteOnly);
        Assert.Equal(mode, actual.Mode);
        Assert.Null(FirmwareFacts.Gpu(null, 0));
        Assert.Null(FirmwareFacts.Gpu(0, 9));
    }

    [Fact(DisplayName = "@important a profile's own preset matches only the custom firmware mode, a firmware mode only its own code; an unknown code matches nothing")]
    public void PowerMatching()
    {
        Assert.True(FirmwareFacts.PowerMatches(FirmwareFacts.Power(255)!, PowerSetting.Own));
        Assert.False(FirmwareFacts.PowerMatches(FirmwareFacts.Power(2)!, PowerSetting.Own));
        Assert.True(FirmwareFacts.PowerMatches(FirmwareFacts.Power(1)!, new PowerSetting(FirmwareMode.Quiet)));
        Assert.False(FirmwareFacts.PowerMatches(FirmwareFacts.Power(255)!, new PowerSetting(FirmwareMode.Quiet)));
        Assert.True(FirmwareFacts.PowerMatches(FirmwareFacts.Power(3)!, new PowerSetting(FirmwareMode.Performance)));
        Assert.False(FirmwareFacts.PowerMatches(FirmwareFacts.Power(77)!, new PowerSetting(FirmwareMode.Balance)));
        Assert.Equal(PowerKind.Extreme, FirmwareFacts.Power(224)!.Kind);
        Assert.Null(FirmwareFacts.Power(null));
    }
}
