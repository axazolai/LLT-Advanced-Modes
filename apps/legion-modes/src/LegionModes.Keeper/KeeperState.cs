using LegionModes.Core;
using LegionModes.Core.Status;
using LegionModes.Updates;

namespace LegionModes.Keeper;

internal sealed record KeeperStateFile(string? InstalledInstaller, UpdatesFile Updates);

// keeper-state.json (everything the keeper keeps) and updates.json (what the plugin reads), written together.
internal sealed class KeeperState
{
    readonly object _gate = new();
    readonly string _statePath = Path.Combine(Paths.DataRoot(), "keeper-state.json");
    readonly string _updatesPath = Path.Combine(Paths.DataRoot(), "updates.json");
    UpdatesFile _updates;
    string? _installedInstaller;

    public KeeperState(string installedVersion)
    {
        var file = JsonFile.Load<KeeperStateFile>(_statePath);
        _installedInstaller = file?.InstalledInstaller;
        _updates = (file?.Updates ?? UpdatesFile.Empty(installedVersion)) with { InstalledVersion = installedVersion };
        Persist();
    }

    // The full outcome of the last check (asset URL and hash included) lives in memory only.
    public CheckOutcome? LastOutcome { get; set; }

    public UpdatesFile Updates { get { lock (_gate) return _updates; } }

    public string? InstalledInstaller
    {
        get { lock (_gate) return _installedInstaller; }
        set { lock (_gate) { _installedInstaller = value; Persist(); } }
    }

    public void Update(Func<UpdatesFile, UpdatesFile> change)
    {
        lock (_gate) { _updates = change(_updates); Persist(); }
    }

    void Persist()
    {
        try
        {
            _updates.Save(_updatesPath);
            JsonFile.Save(_statePath, new KeeperStateFile(_installedInstaller, _updates));
        }
        catch (Exception e) { KeeperLog.Write("state write failed: " + e.Message); }
    }

    public static CheckSummary Summarize(CheckOutcome o) => new(
        o.Release is null ? null : new ReleaseSummary(o.Release.Version, o.Release.Tag, o.Release.Published, o.Release.Body),
        o.Report?.Compatible,
        o.Report?.Problems ?? [],
        o.Report?.ReferencesChecked ?? 0,
        o.Changelog.Select(c => new ChangelogLine(c.Line, c.Highlight)).ToList(),
        o.Error);
}
