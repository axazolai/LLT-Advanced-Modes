using Timer = System.Threading.Timer;
using System.ComponentModel;
using System.IO;
using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Core.Status;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Messaging;
using LenovoLegionToolkit.Lib.Messaging.Messages;
using LenovoLegionToolkit.Lib.Settings;

namespace LegionModes.Plugin.Shell;

// Notifications go through LLT's own notification window (the automation notification type). Raised for: an apply step
// failing (once per step until it succeeds), a compatible update found (once per version), and the entries the keeper and
// the guard left in notifications-pending.json, and a change of the profile in force (the power-mode type, so LLT draws it
// like its own power mode change and its «Power mode» notification setting governs it).
internal sealed class Notifier : IDisposable
{
    static readonly TimeSpan Period = TimeSpan.FromSeconds(60);

    readonly Runtime _runtime;
    readonly string _dataDir;
    readonly HashSet<ParameterId> _failed = [];
    readonly object _gate = new();
    Timer? _timer;
    int _checking;
    int _disposed;

    public Notifier(Runtime runtime, string dataDir)
    {
        _runtime = runtime;
        _dataDir = dataDir;
    }

    string PendingPath => Path.Combine(Paths.DataRoot(), "notifications-pending.json");
    string NotifiedUpdatePath => Path.Combine(_dataDir, "notified-update.txt");

    public void Start()
    {
        try
        {
            var settings = IoCContainer.Resolve<NotificationSettings>();
            if (!settings.Store.Notifications.AutomationNotification)
            {
                settings.Store.Notifications.AutomationNotification = true;
                settings.SynchronizeStore();
            }
        }
        catch (Exception e) { Log.Error("turn on automation notifications", e); }
        _runtime.Status.PropertyChanged += OnStatusChanged;
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.Zero, Period);
    }

    public static void Show(string text)
    {
        try { MessagingCenter.Publish(new NotificationMessage(NotificationType.AutomationNotification, text)); }
        catch (Exception e) { Log.Error("publish notification", e); }
    }

    public static void ShowProfile(string name)
    {
        try { MessagingCenter.Publish(new NotificationMessage(NotificationType.PowerModeGodMode, Tr.F("Profile: {0}", name))); }
        catch (Exception e) { Log.Error("publish profile notification", e); }
    }

    void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            if (e.PropertyName != nameof(StatusModel.ParameterResults)) return;
            var results = _runtime.Status.ParameterResults;
            var texts = new List<string>();
            lock (_gate)
            {
                foreach (var (parameter, result) in results)
                {
                    var failed = result != "ok" && result != "skipped" && !result.StartsWith(StatusModel.NotePrefix, StringComparison.Ordinal);
                    if (!failed) { _failed.Remove(parameter); continue; }
                    if (!_failed.Add(parameter)) continue;
                    var name = Ui.Texts.Parameter.TryGetValue(parameter, out var n) ? n : parameter.ToString();
                    texts.Add(Tr.F("Advanced modes: could not apply \"{0}\": {1}", name, result));
                }
            }
            foreach (var t in texts) Show(t);
        }
        catch (Exception ex) { Log.Error("apply failure notification", ex); }
    }

    // Pending entries and the update check; one run at a time.
    async Task CheckAsync()
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            foreach (var text in await Task.Run(() => PendingNotifications.TakeAll(PendingPath)).ConfigureAwait(false))
                Show(text);
        }
        catch (Exception e) { Log.Error("pending notifications", e); }
        try
        {
            var file = await Task.Run(() => UpdatesFile.LoadShared(Path.Combine(Paths.DataRoot(), "updates.json"))).ConfigureAwait(false);
            if (file is { HasCompatibleUpdate: true, Last.Release: { } release })
            {
                var seen = File.Exists(NotifiedUpdatePath) ? File.ReadAllText(NotifiedUpdatePath).Trim() : "";
                if (seen != release.Version)
                {
                    Show(Tr.F("Advanced modes: LLT update {0} is available, compatible with the plugin", release.Version));
                    Directory.CreateDirectory(_dataDir);
                    File.WriteAllText(NotifiedUpdatePath, release.Version);
                }
            }
        }
        catch (Exception e) { Log.Error("update notification", e); }
        finally { Volatile.Write(ref _checking, 0); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _runtime.Status.PropertyChanged -= OnStatusChanged; } catch { }
        try { _timer?.Dispose(); } catch (Exception e) { Log.Error("dispose notifier timer", e); }
    }
}
