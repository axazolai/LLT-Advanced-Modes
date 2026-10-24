using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using LegionModes.Core.Sensors;

namespace LegionModes.Tray;

// The 32x32 tray icon: a thin rounded frame of a fixed colour per sensor and the number in the colour of its temperature zone,
// on a transparent background. Windows scales it to the screen.
public static class IconRenderer
{
    public const int Size = 32;

    public static Color FrameColor(Sensor sensor) =>
        sensor == Sensor.Cpu ? Color.FromArgb(0xB7, 0x1C, 0x1C) : Color.FromArgb(0x1B, 0x5E, 0x20);

    // zone null = the failure look: grey.
    public static Color DigitColor(HeatZone? zone) => zone switch
    {
        HeatZone.Green => Color.FromArgb(0x3D, 0xDC, 0x84),
        HeatZone.Yellow => Color.FromArgb(0xFF, 0xD5, 0x4F),
        HeatZone.Orange => Color.FromArgb(0xFF, 0x98, 0x00),
        HeatZone.Red => Color.FromArgb(0xFF, 0x52, 0x52),
        _ => Color.FromArgb(0x9E, 0x9E, 0x9E),
    };

    public static Bitmap Render(Sensor sensor, string digits, HeatZone? zone)
    {
        var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using (var pen = new Pen(FrameColor(sensor), 2f))
        using (var path = RoundedRectangle(1f, 1f, Size - 2f, Size - 2f, 6f))
            g.DrawPath(pen, path);
        using var font = new Font("Segoe UI", digits.Length >= 3 ? 12f : 17f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(DigitColor(zone));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(digits, font, brush, new RectangleF(0, 0, Size, Size), format);
        return bitmap;
    }

    static GraphicsPath RoundedRectangle(float x, float y, float w, float h, float r)
    {
        var d = r * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
