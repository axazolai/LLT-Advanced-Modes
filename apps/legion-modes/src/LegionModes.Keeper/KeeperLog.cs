using System.Diagnostics;
using LegionModes.Core;

namespace LegionModes.Keeper;

// keeper.log in the data root: rolls at 1 MB, three files (keeper.log, .1, .2). Never throws.
internal static class KeeperLog
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
                var path = Path.Combine(dir, "keeper.log");
                Roll(path);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
        }
        catch { /* logging never breaks the keeper */ }
    }

    static void Roll(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes) return;
        if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2", overwrite: true);
        File.Move(path, path + ".1", overwrite: true);
    }

    // Application event log, source "LegionModes". Creating the source needs the elevated keeper.
    public static void Event(string text, EventLogEntryType type)
    {
        try
        {
            if (!EventLog.SourceExists(Paths.PluginId)) EventLog.CreateEventSource(Paths.PluginId, "Application");
            EventLog.WriteEntry(Paths.PluginId, text, type);
        }
        catch (Exception e) { Write("event log failed: " + e.Message); }
    }
}
