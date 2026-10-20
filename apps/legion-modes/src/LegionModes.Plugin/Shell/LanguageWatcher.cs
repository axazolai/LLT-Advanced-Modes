using System.IO;
using LegionModes.Core;

namespace LegionModes.Plugin.Shell;

// LLT writes its language code to <LLT data>\lang when the owner changes the language. The change is read at once (Tr.Changed
// follows), debounced against the several events one write raises. Never throws.
internal sealed class LanguageWatcher : IDisposable
{
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    readonly FileSystemWatcher? _watcher;
    readonly System.Threading.Timer _timer;

    public LanguageWatcher()
    {
        _timer = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(Paths.LltDataDir, "lang") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            _watcher.Changed += (_, _) => Kick();
            _watcher.Created += (_, _) => Kick();
            _watcher.Renamed += (_, _) => Kick();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) { Log.Error("language watcher", e); }
    }

    void Kick()
    {
        try { _timer.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* disposed while an event was pending */ }
    }

    static void Reload()
    {
        try { Tr.FromLlt(); }
        catch (Exception e) { Log.Error("language reload", e); }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
