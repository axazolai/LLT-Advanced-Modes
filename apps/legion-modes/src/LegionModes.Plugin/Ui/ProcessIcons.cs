using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LegionModes.Plugin.Ui;

// Icons and file descriptions of executables, read once per path. Safe off the UI thread: the images are frozen.
internal static class ProcessIcons
{
    static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, string?> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    // The exe's associated icon; null when it cannot be read (the caller shows a generic one).
    public static ImageSource? Of(string? path) => path is null ? null : Icons.GetOrAdd(path, Read);

    // The icon already read, without touching the disk (for the UI thread); false when it was not read yet.
    public static bool TryCached(string path, out ImageSource? image) => Icons.TryGetValue(path, out image);

    // The version resource's FileDescription; null when absent.
    public static string? Description(string? path) => path is null ? null : Descriptions.GetOrAdd(path, p =>
    {
        try { return FileVersionInfo.GetVersionInfo(p).FileDescription is { Length: > 0 } d ? d.Trim() : null; }
        catch { return null; }
    });

    static ImageSource? Read(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
