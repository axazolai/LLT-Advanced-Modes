using System.Text.Json.Serialization;

namespace LegionModes.Core.Status;

public sealed record ChangelogLine(string Line, bool Highlight);

public sealed record ReleaseSummary(string Version, string Tag, DateTimeOffset Published, string Body);

// The last check in plain types: the keeper writes it, the plugin's Updates tab reads it.
public sealed record CheckSummary(
    ReleaseSummary? Release, bool? Compatible, IReadOnlyList<string> Problems, int ReferencesChecked,
    IReadOnlyList<ChangelogLine> Changelog, string? Error);

public sealed record HistoryEntry(
    DateTimeOffset At, string Version, string Tag, bool Success, string FailedAt, string? Error, string BackupDir);

public sealed record UpdatesFile(
    DateTimeOffset? LastCheck, DateTimeOffset? NextCheck, string InstalledVersion, CheckSummary? Last,
    IReadOnlyList<HistoryEntry> History)
{
    public static UpdatesFile Empty(string installedVersion) => new(null, null, installedVersion, null, []);

    public static UpdatesFile? Load(string path) => JsonFile.Load<UpdatesFile>(path);

    // The keeper replaces the file; a read during the replace gives null. A missing file gives null at once, otherwise the
    // read is retried up to 3 times, 200 ms apart. Blocks the calling thread: call it off the UI thread.
    public static UpdatesFile? LoadShared(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) Thread.Sleep(200);
            if (!File.Exists(path)) return null;
            if (Load(path) is { } file) return file;
        }
        return null;
    }

    // A compatible release other than the installed version.
    [JsonIgnore]
    public bool HasCompatibleUpdate => Last is { Compatible: true, Release: { } r } && r.Version != InstalledVersion;

    public void Save(string path) => JsonFile.Save(path, this);
}
