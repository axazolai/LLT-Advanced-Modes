using LegionModes.Core;

namespace LegionModes.Guard;

// guard.log in the data root: rolls at 1 MB, three files. Never throws.
internal static class GuardLog
{
    const long MaxBytes = 1_000_000;
    static readonly object Gate = new();

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                var dir = Paths.DataRoot();
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "guard.log");
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                {
                    if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2", overwrite: true);
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
        }
        catch { /* logging never breaks the guard */ }
    }
}
