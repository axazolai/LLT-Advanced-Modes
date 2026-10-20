using System.Text.Encodings.Web;
using System.Text.Json;

namespace LegionModes.Updates;

// One JSON line per update attempt, carrying the full reconciliation report.
public static class UpdateHistory
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Line(UpdateOutcome o, ReleaseInfo r, DateTimeOffset at) => JsonSerializer.Serialize(new
    {
        at,
        version = r.Version,
        tag = r.Tag,
        success = o.Success,
        failedAt = o.FailedAt.ToString(),
        error = o.Error,
        backupDir = o.BackupDir,
        reconcile = o.Reconcile is null ? null : new
        {
            restored = o.Reconcile.Restored,
            added = o.Reconcile.Added,
            removed = o.Reconcile.Removed,
            keyFailures = o.Reconcile.KeyFailures
        }
    }, Options);

    public static void Append(string path, UpdateOutcome o, ReleaseInfo r, DateTimeOffset at)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, Line(o, r, at) + Environment.NewLine);
    }
}
