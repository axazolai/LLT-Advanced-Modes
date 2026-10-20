using LegionModes.Core.Ipc;

namespace LegionModes.Updates;

public sealed record ReleaseInfo(string Version, string Tag, string AssetUrl, string AssetSha256, string Body, DateTimeOffset Published);

public sealed record CheckOutcome(ReleaseInfo? Release, CompatibilityReport? Report,
    IReadOnlyList<(string Line, bool Highlight)> Changelog, string? Error);   // Error: network, hash, signer, extraction

public enum UpdateStep
{
    Download, Verify, Extract, Compat, Backup, Install, MigrationStart, Reconcile, SelfTest, Done,
    Rollback, RollbackFailed
}

public sealed record UpdateOutcome(bool Success, UpdateStep FailedAt, string? Error, ReconcileResult? Reconcile, string BackupDir);

public interface IUpdateOps
{
    Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct);          // null when none newer than installed
    string InstalledVersion();
    Task<string> DownloadAsync(ReleaseInfo r, CancellationToken ct);          // returns installer path
    bool VerifyHash(string installer, string sha256);
    bool VerifySigner(string installer, string thumbprint);
    string Extract(string installer);                                          // returns {app} dir
    CompatibilityReport CheckCompat(string hostDir, string pluginDll);        // pluginDll: the plugin that will run on that host
    string? NextPluginDir(string version);                                     // C:\Program Files\LegionModes\plugin\<version>\ when the
                                                                               // package carries a plugin built for that LLT version;
                                                                               // null = keep the installed one
    void InstallPlugin(string pluginDir);                                      // mirror into %LOCALAPPDATA%\LenovoLegionToolkit\Plugins\LegionModes
    IReadOnlyList<string> PluginVersions();                                    // the <version> folder names under ...\LegionModes\plugin\
    string Backup(string installedInstaller);                                  // returns backup dir
    Task NotifyGuardAsync(bool updateActive);
    Task<bool> StopLltGracefullyAsync(TimeSpan timeout);                       // asks the plugin, waits for exit
    int RunInstaller(string installer);                                        // exit code
    Task<bool> StartLltAndWaitLoadedAsync(TimeSpan timeout);
    Task<SelfTestReport?> StartLltAndWaitSelfTestAsync(TimeSpan timeout);
    ReconcileResult Reconcile(string backupDir, bool hasControl);
    void EnforceAfterInstall();                                                // remove LLT autorun task, update check Never
    bool HasControl();
    void RestoreBackup(string backupDir);                                      // both folders as mirrors: files absent
                                                                               // from the backup are deleted
    string InstallerOf(string backupDir);
    void RecordHistory(UpdateOutcome outcome, ReleaseInfo release);
    void Notify(string text);
}
