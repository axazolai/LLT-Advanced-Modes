using LegionModes.Core;

namespace LegionModes.Updates;

public sealed class UpdateOrchestrator(IUpdateOps ops)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

    public async Task<CheckOutcome> CheckAsync(CancellationToken ct)
    {
        ReleaseInfo? release = null;
        try
        {
            release = await ops.GetLatestReleaseAsync(ct);
            if (release is null) return new CheckOutcome(null, null, [], null);
            var installer = await ops.DownloadAsync(release, ct);
            if (!ops.VerifyHash(installer, release.AssetSha256))
                return new CheckOutcome(release, null, [], "hash mismatch");
            if (!ops.VerifySigner(installer, Pins.SignerThumbprint))
                return new CheckOutcome(release, null, [], "signer mismatch");
            string hostDir;
            try { hostDir = ops.Extract(installer); }
            catch (Exception ex) { return new CheckOutcome(release, null, [], "extraction failed: " + ex.Message); }
            var report = ops.CheckCompat(hostDir, PluginDllFor(ops.NextPluginDir(release.Version)));
            return new CheckOutcome(release, report, ChangelogHighlighter.Highlight(release.Body), null);
        }
        catch (Exception ex)
        {
            return new CheckOutcome(release, null, [], ex.Message);
        }
    }

    public async Task<UpdateOutcome> UpdateAsync(ReleaseInfo release, string installedInstaller, CancellationToken ct)
    {
        var backup = "";
        var step = UpdateStep.Download;
        string installer;
        bool hasControl;
        string? nextPlugin;

        // Before Backup: any failure only stops; the guard was never told about an update.
        try
        {
            installer = await ops.DownloadAsync(release, ct);
            step = UpdateStep.Verify;
            if (!ops.VerifyHash(installer, release.AssetSha256))
                return Finish(release, Stop(UpdateStep.Verify, "hash mismatch"));
            if (!ops.VerifySigner(installer, Pins.SignerThumbprint))
                return Finish(release, Stop(UpdateStep.Verify, "signer mismatch"));
            step = UpdateStep.Extract;
            var hostDir = ops.Extract(installer);
            step = UpdateStep.Compat;
            nextPlugin = ops.NextPluginDir(release.Version);
            var report = ops.CheckCompat(hostDir, PluginDllFor(nextPlugin));
            if (!report.Compatible)
                return Finish(release, Stop(UpdateStep.Compat, string.Join("; ", report.Problems)));
            step = UpdateStep.Backup;
            hasControl = ops.HasControl();
            ct.ThrowIfCancellationRequested();
            backup = ops.Backup(installedInstaller);
        }
        catch (Exception ex)
        {
            return Finish(release, Stop(step, ex.Message));
        }

        // From here on ct is not honoured: the flow runs to its end.
        UpdateOutcome outcome;
        step = UpdateStep.Install;
        ReconcileResult? rec = null;
        var installStarted = false;
        var stopAttempted = false;
        try
        {
            await ops.NotifyGuardAsync(true);
            stopAttempted = true;
            var stopped = await ops.StopLltGracefullyAsync(StopTimeout);
            if (!stopped)
            {
                outcome = Stop(UpdateStep.Install, Tr.T("LLT did not close"), backup);   // nothing installed yet
                await RestartLltAsync();
            }
            else if ((installStarted = true) && ops.RunInstaller(installer) != 0)
                outcome = await RollbackAsync(UpdateStep.Install, Tr.T("the LLT installer returned an error"), backup, null);
            else
            {
                ops.EnforceAfterInstall();
                if (nextPlugin is not null) ops.InstallPlugin(nextPlugin);   // a throw rolls back like any later step
                step = UpdateStep.MigrationStart;
                if (!await ops.StartLltAndWaitLoadedAsync(StartTimeout))
                    outcome = await RollbackAsync(UpdateStep.MigrationStart, Tr.T("LLT did not report loading"), backup, null);
                else if (!await ops.StopLltGracefullyAsync(StopTimeout))
                    outcome = await RollbackAsync(UpdateStep.MigrationStart, Tr.T("LLT did not close"), backup, null);
                else
                {
                    step = UpdateStep.Reconcile;
                    rec = ops.Reconcile(backup, hasControl);
                    if (!rec.Ok)
                        outcome = await RollbackAsync(UpdateStep.Reconcile, string.Join("; ", rec.KeyFailures), backup, rec);
                    else
                    {
                        step = UpdateStep.SelfTest;
                        var st = await ops.StartLltAndWaitSelfTestAsync(StartTimeout);
                        outcome = st is { Ok: true }
                            ? new UpdateOutcome(true, UpdateStep.Done, null, rec, backup)
                            : await RollbackAsync(UpdateStep.SelfTest,
                                st is null ? Tr.T("the self-check did not answer") : string.Join("; ", st.Failures), backup, rec);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (installStarted)
                outcome = await RollbackAsync(step, ex.Message, backup, rec);
            else
            {
                outcome = Stop(UpdateStep.Install, ex.Message, backup);   // nothing installed yet
                if (stopAttempted) await RestartLltAsync();
            }
        }
        finally
        {
            try { await ops.NotifyGuardAsync(false); } catch { /* the outcome is already decided */ }
        }
        return Finish(release, outcome);
    }

    private static string PluginDllFor(string? nextPluginDir) =>
        nextPluginDir is { } d ? Path.Combine(d, "LegionModes.dll") : Paths.PluginDll;

    // After a stop that followed a StopLlt attempt: give the owner LLT back. Result ignored.
    private async Task RestartLltAsync()
    {
        try { await ops.StartLltAndWaitLoadedAsync(StartTimeout); } catch { /* best effort */ }
    }

    private UpdateOutcome Finish(ReleaseInfo release, UpdateOutcome outcome)
    {
        try { ops.RecordHistory(outcome, release); } catch { /* a history failure never changes the outcome */ }
        return outcome;
    }

    private static UpdateOutcome Stop(UpdateStep step, string? error = null, string backup = "") =>
        new(false, step, error ?? step.ToString(), null, backup);

    private async Task<UpdateOutcome> RollbackAsync(UpdateStep failedAt, string error, string backup, ReconcileResult? rec)
    {
        var problems = new List<string>();
        try { await ops.StopLltGracefullyAsync(StopTimeout); } catch { /* ignored */ }
        try
        {
            var backupInstaller = ops.InstallerOf(backup);
            if (!ops.VerifySigner(backupInstaller, Pins.SignerThumbprint)) problems.Add(Tr.T("the installer from the backup is not signed by the pinned publisher; not run"));
            else if (ops.RunInstaller(backupInstaller) != 0) problems.Add(Tr.T("the installer from the backup returned an error"));
        }
        catch (Exception ex) { problems.Add(Tr.F("the installer from the backup crashed: {0}", ex.Message)); }
        try { ops.RestoreBackup(backup); }
        catch (Exception ex) { problems.Add(Tr.F("file restore crashed: {0}", ex.Message)); }
        try
        {
            var st = await ops.StartLltAndWaitSelfTestAsync(StartTimeout);
            if (st is null) problems.Add(Tr.T("the self-check after the rollback did not answer"));
            else if (!st.Ok) problems.Add(Tr.F("self-check after the rollback: {0}", string.Join("; ", st.Failures)));
        }
        catch (Exception ex) { problems.Add(Tr.F("the self-check after the rollback crashed: {0}", ex.Message)); }

        if (problems.Count == 0) return new UpdateOutcome(false, failedAt, error, rec, backup);
        var text = Tr.F("{0}: {1}; rollback failed: {2}", failedAt, error, string.Join("; ", problems));
        try { ops.Notify(Tr.F("The LLT update failed and the rollback failed. Backup: {0}. {1}", backup, text)); } catch { /* best effort */ }
        return new UpdateOutcome(false, UpdateStep.RollbackFailed, text, rec, backup);
    }
}
