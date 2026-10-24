using System.Drawing;
using LegionModes.Core.Sensors;
using LegionModes.Tray;
using Xunit;

namespace LegionModes.Tests;

public class IconRendererTests
{
    private static bool Near(Color c, Color want, int tol) =>
        Math.Abs(c.R - want.R) <= tol && Math.Abs(c.G - want.G) <= tol && Math.Abs(c.B - want.B) <= tol;

    private static List<(int X, int Y)> Pixels(Bitmap b, Color want, int tol)
    {
        var found = new List<(int, int)>();
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
            {
                var c = b.GetPixel(x, y);
                if (c.A == 255 && Near(c, want, tol)) found.Add((x, y));
            }
        return found;
    }

    [Fact(DisplayName = "@important the CPU frame is dark red, the dGPU frame dark green, the corner is transparent")]
    public void FrameColours()
    {
        using var cpu = IconRenderer.Render(Sensor.Cpu, "49", HeatZone.Green);
        using var gpu = IconRenderer.Render(Sensor.Dgpu, "49", HeatZone.Green);
        Assert.Equal(32, cpu.Width);
        Assert.Equal(32, cpu.Height);
        Assert.Equal(Color.FromArgb(255, 0xB7, 0x1C, 0x1C), cpu.GetPixel(1, 16));
        Assert.Equal(Color.FromArgb(255, 0x1B, 0x5E, 0x20), gpu.GetPixel(1, 16));
        Assert.Equal(0, cpu.GetPixel(0, 0).A);
        Assert.Equal(0, gpu.GetPixel(0, 0).A);
    }

    [Fact(DisplayName = "@important the digits take the colour of the zone, each zone has its own colour, no value is grey")]
    public void DigitColours()
    {
        using var green = IconRenderer.Render(Sensor.Cpu, "49", HeatZone.Green);
        Assert.True(Pixels(green, Color.FromArgb(0x3D, 0xDC, 0x84), 40).Count >= 15);
        Assert.Empty(Pixels(green, Color.FromArgb(0xFF, 0x52, 0x52), 40));

        var zones = new[]
        {
            (HeatZone.Green, Color.FromArgb(0x3D, 0xDC, 0x84)),
            (HeatZone.Yellow, Color.FromArgb(0xFF, 0xD5, 0x4F)),
            (HeatZone.Orange, Color.FromArgb(0xFF, 0x98, 0x00)),
            (HeatZone.Red, Color.FromArgb(0xFF, 0x52, 0x52)),
        };
        foreach (var (zone, colour) in zones)
        {
            using var b = IconRenderer.Render(Sensor.Dgpu, "88", zone);
            Assert.True(Pixels(b, colour, 40).Count >= 15, zone.ToString());
        }

        using var grey = IconRenderer.Render(Sensor.Cpu, "--", null);
        Assert.True(Pixels(grey, Color.FromArgb(0x9E, 0x9E, 0x9E), 40).Count >= 4);
        Assert.Empty(Pixels(grey, Color.FromArgb(0x3D, 0xDC, 0x84), 40));
    }

    [Fact(DisplayName = "@important a three-digit value stays inside the frame with a font no larger than the two-digit one")]
    public void ThreeDigits()
    {
        var colour = Color.FromArgb(0xFF, 0x52, 0x52);
        using var three = IconRenderer.Render(Sensor.Cpu, "101", HeatZone.Red);
        using var two = IconRenderer.Render(Sensor.Cpu, "99", HeatZone.Red);
        var p3 = Pixels(three, colour, 40);
        var p2 = Pixels(two, colour, 40);
        Assert.NotEmpty(p3);
        Assert.True(p3.Min(p => p.X) >= 3);
        Assert.True(p3.Max(p => p.X) <= 28);
        Assert.True(p3.Max(p => p.Y) - p3.Min(p => p.Y) <= p2.Max(p => p.Y) - p2.Min(p => p.Y));
    }
}
