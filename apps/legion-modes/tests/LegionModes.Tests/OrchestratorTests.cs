using LegionModes.Core.Ipc;
using LegionModes.Updates;

namespace LegionModes.Tests;

public sealed class OrchestratorTests
{
    private sealed class FakeOps : IUpdateOps
    {
        public List<string> Log = [];
        public bool HashOk = true, SignerOk = true, Compatible = true, Control = true;
        public bool StopOk = true, StopOkSecond = true, LoadedOk = true;
        public int InstallExit, RollbackExit;
        public bool GuardTrueThrows, FirstStopThrows, BackupInstallerThrows;
        public bool ReconcileFails, ReconcileThrows, RestoreThrows, ExtractThrows;
        public SelfTestReport? SelfTest = new(true, []);
        public SelfTestReport? RollbackSelfTest = new(true, []);
        public Exception? LatestThrows;
        public ReleaseInfo? Latest = Release;
        public List<string> Notes = [];
        public UpdateOutcome? Recorded;
        private int _stops, _selfTests;

        public Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct)
        {
            if (LatestThrows is not null) throw LatestThrows;
            return Task.FromResult(Latest);
        }
        public string InstalledVersion() => "2.34.0.0";
        public Task<string> DownloadAsync(ReleaseInfo r, CancellationToken ct) { Log.Add("Download"); return Task.FromResult("new.exe"); }
        public bool VerifyHash(string i, string s) { Log.Add("VerifyHash"); return HashOk; }
        public bool VerifySigner(string i, string t) { Log.Add("VerifySigner:" + t); return SignerOk; }
        public string Extract(string i) { Log.Add("Extract"); if (ExtractThrows) throw new IOException("bad archive"); return "app"; }
        public string? NextDir, CompatPluginDll;
        public bool InstallPluginThrows;
        public string? NextPluginDir(string v) => NextDir;
        public void InstallPlugin(string d) { Log.Add("InstallPlugin:" + d); if (InstallPluginThrows) throw new IOException("plugin copy"); }
        public IReadOnlyList<string> PluginVersions() => [];
        public CompatibilityReport CheckCompat(string d, string pluginDll) { Log.Add("CheckCompat"); CompatPluginDll = pluginDll; return new(Compatible, Compatible ? [] : ["x"], 1); }
        public string Backup(string i) { Log.Add("Backup"); return "bk"; }
        public Task NotifyGuardAsync(bool a)
        {
            Log.Add("NotifyGuard(" + (a ? "true" : "false") + ")");
            if (a && GuardTrueThrows) throw new IOException("guard pipe");
            return Task.CompletedTask;
        }
        public Task<bool> StopLltGracefullyAsync(TimeSpan t)
        {
            Log.Add("StopLlt");
            if (FirstStopThrows && _stops == 0) { _stops++; throw new IOException("stop pipe"); }
            return Task.FromResult(++_stops == 1 ? StopOk : StopOkSecond);
        }
        public int RunInstaller(string i) { Log.Add("RunInstaller:" + i); if (i == "bk.exe" && BackupInstallerThrows) throw new FileNotFoundException("no saved installer"); return i == "bk.exe" ? RollbackExit : InstallExit; }
        public Task<bool> StartLltAndWaitLoadedAsync(TimeSpan t) { Log.Add("StartLltAndWaitLoaded"); return Task.FromResult(LoadedOk); }
        public Task<SelfTestReport?> StartLltAndWaitSelfTestAsync(TimeSpan t)
        {
            Log.Add("StartLltAndWaitSelfTest");
            return Task.FromResult(++_selfTests == 1 && !Log.Contains("RestoreBackup") ? SelfTest : RollbackSelfTest);
        }
        public ReconcileResult Reconcile(string b, bool c)
        {
            Log.Add("Reconcile");
            if (ReconcileThrows) throw new InvalidOperationException("boom");
            return new([], [], [], ReconcileFails ? ["key lost"] : []);
        }
        public void EnforceAfterInstall() => Log.Add("EnforceAfterInstall");
        public bool HasControl() => Control;
        public void RestoreBackup(string b) { Log.Add("RestoreBackup"); if (RestoreThrows) throw new IOException("disk"); }
        public string InstallerOf(string b) => b + ".exe";
        public void RecordHistory(UpdateOutcome o, ReleaseInfo r) { Log.Add("RecordHistory"); Recorded = o; }
        public void Notify(string text) { Log.Add("Notify"); Notes.Add(text); }
    }

    private static readonly ReleaseInfo Release = new("2.35.0.0", "v2.35.0.0", "https://x/y.exe", "abc",
        "- fixed a thing\n- added a thing", DateTimeOffset.UnixEpoch);

    private static Task<UpdateOutcome> Run(FakeOps f) =>
        new UpdateOrchestrator(f).UpdateAsync(Release, "old.exe", CancellationToken.None);

    private static void AssertRolledBack(FakeOps f)
    {
        var i = f.Log.IndexOf("RunInstaller:bk.exe");
        Assert.True(i > 0, "installer-of-backup not run");
        var r = f.Log.IndexOf("RestoreBackup");
        Assert.True(r > i, "RestoreBackup missing or before installer");
        Assert.True(f.Log.IndexOf("StartLltAndWaitSelfTest", r) > r, "no self-test after restore");
        var g = f.Log.LastIndexOf("NotifyGuard(false)");
        Assert.True(g > f.Log.LastIndexOf("StartLltAndWaitSelfTest"));
        Assert.Equal("RecordHistory", f.Log[^1]);
        Assert.Equal(g + 1, f.Log.Count - 1);
    }

    [Theory(DisplayName = "@critical failure at each step rolls back and ends with guard off")]
    [InlineData(UpdateStep.Install)]
    [InlineData(UpdateStep.MigrationStart)]
    [InlineData(UpdateStep.Reconcile)]
    [InlineData(UpdateStep.SelfTest)]
    public async Task FailureRollsBack(UpdateStep step)
    {
        var f = new FakeOps();
        switch (step)
        {
            case UpdateStep.Install: f.InstallExit = 1; break;
            case UpdateStep.MigrationStart: f.LoadedOk = false; break;
            case UpdateStep.Reconcile: f.ReconcileFails = true; break;
            case UpdateStep.SelfTest: f.SelfTest = new(false, ["pipe dead"]); break;
        }
        var o = await Run(f);
        Assert.False(o.Success);
        Assert.Equal(step, o.FailedAt);
        AssertRolledBack(f);
        Assert.Empty(f.Notes);
    }

    [Fact(DisplayName = "@critical second stop failing in migration start rolls back at MigrationStart")]
    public async Task SecondStopFailure()
    {
        var f = new FakeOps { StopOkSecond = false };
        var o = await Run(f);
        Assert.Equal(UpdateStep.MigrationStart, o.FailedAt);
        AssertRolledBack(f);
    }

    [Fact(DisplayName = "@critical exception from Reconcile after Backup rolls back")]
    public async Task ReconcileThrowsRollsBack()
    {
        var f = new FakeOps { ReconcileThrows = true };
        var o = await Run(f);
        Assert.False(o.Success);
        Assert.Equal(UpdateStep.Reconcile, o.FailedAt);
        Assert.Contains("boom", o.Error);
        AssertRolledBack(f);
    }

    [Theory(DisplayName = "@critical failing rollback gives RollbackFailed and Notify with the backup dir")]
    [InlineData("selftest-bad")]
    [InlineData("selftest-null")]
    [InlineData("installer")]
    [InlineData("restore-throws")]
    public async Task RollbackFailure(string how)
    {
        var f = new FakeOps { SelfTest = new(false, ["pipe dead"]) };
        switch (how)
        {
            case "selftest-bad": f.RollbackSelfTest = new(false, ["old broken"]); break;
            case "selftest-null": f.RollbackSelfTest = null; break;
            case "installer": f.RollbackExit = 5; break;
            case "restore-throws": f.RestoreThrows = true; break;
        }
        var o = await Run(f);
        Assert.False(o.Success);
        Assert.Equal(UpdateStep.RollbackFailed, o.FailedAt);
        Assert.Contains("SelfTest", o.Error);
        Assert.Contains("откат не удался", o.Error);
        Assert.Equal("bk", o.BackupDir);
        Assert.Single(f.Notes);
        Assert.Contains("bk", f.Notes[0]);
        Assert.Equal("RecordHistory", f.Log[^1]);
        Assert.True(f.Log.IndexOf("Notify") < f.Log.LastIndexOf("NotifyGuard(false)"));
    }

    [Fact(DisplayName = "@critical a failing StopLlt during rollback is ignored")]
    public async Task RollbackIgnoresStopFailure()
    {
        var f = new FakeOps { InstallExit = 1 };
        f.StopOkSecond = false;   // second StopLlt call is the rollback's
        var o = await Run(f);
        Assert.Equal(UpdateStep.Install, o.FailedAt);
        AssertRolledBack(f);
    }

    [Theory(DisplayName = "@critical hash, signer or compat failure stops before Backup")]
    [InlineData("hash", UpdateStep.Verify)]
    [InlineData("signer", UpdateStep.Verify)]
    [InlineData("compat", UpdateStep.Compat)]
    public async Task StopsBeforeBackup(string what, UpdateStep step)
    {
        var f = new FakeOps();
        if (what == "hash") f.HashOk = false;
        if (what == "signer") f.SignerOk = false;
        if (what == "compat") f.Compatible = false;
        var o = await Run(f);
        Assert.False(o.Success);
        Assert.Equal(step, o.FailedAt);
        if (what != "compat") Assert.Contains(what, o.Error);
        Assert.DoesNotContain("Backup", f.Log);
        Assert.DoesNotContain(f.Log, l => l.StartsWith("RunInstaller"));
        Assert.DoesNotContain("RestoreBackup", f.Log);
        Assert.Equal("RecordHistory", f.Log[^1]);
    }

    [Fact(DisplayName = "@critical an exception before Backup stops with its message and installs nothing")]
    public async Task ExceptionBeforeBackup()
    {
        var f = new FakeOps { ExtractThrows = true };
        var o = await Run(f);
        Assert.Equal(UpdateStep.Extract, o.FailedAt);
        Assert.Contains("bad archive", o.Error);
        Assert.DoesNotContain("Backup", f.Log);
        Assert.DoesNotContain(f.Log, l => l.StartsWith("RunInstaller"));
    }

    [Fact(DisplayName = "@critical a cancelled token stops before Backup")]
    public async Task CancelledBeforeBackup()
    {
        var f = new FakeOps();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var o = await new UpdateOrchestrator(f).UpdateAsync(Release, "old.exe", cts.Token);
        Assert.False(o.Success);
        Assert.Equal(UpdateStep.Backup, o.FailedAt);
        Assert.DoesNotContain("Backup", f.Log);
    }

    [Fact(DisplayName = "@critical LLT not exiting before the install stops at Install without installing or restoring")]
    public async Task LltDoesNotExit()
    {
        var f = new FakeOps { StopOk = false };
        var o = await Run(f);
        Assert.False(o.Success);
        Assert.Equal(UpdateStep.Install, o.FailedAt);
        Assert.DoesNotContain(f.Log, l => l.StartsWith("RunInstaller"));
        Assert.DoesNotContain("RestoreBackup", f.Log);
        Assert.Contains("NotifyGuard(false)", f.Log);
        Assert.Contains("StartLltAndWaitLoaded", f.Log);
        Assert.Equal("RecordHistory", f.Log[^1]);
    }

    [Fact(DisplayName = "@important happy path runs the steps in order and ends Done")]
    public async Task HappyPath()
    {
        var f = new FakeOps();
        var o = await Run(f);
        Assert.True(o.Success);
        Assert.Equal(UpdateStep.Done, o.FailedAt);
        var flow = f.Log.SkipWhile(l => l != "Backup").ToArray();
        Assert.Equal(["Backup", "NotifyGuard(true)", "StopLlt", "RunInstaller:new.exe", "EnforceAfterInstall",
            "StartLltAndWaitLoaded", "StopLlt", "Reconcile", "StartLltAndWaitSelfTest", "NotifyGuard(false)",
            "RecordHistory"], flow);
        Assert.Equal(o, f.Recorded);
    }

    [Fact(DisplayName = "@important CheckAsync with a throwing release lookup gives Error, not an exception")]
    public async Task CheckCatchesNetwork()
    {
        var f = new FakeOps { LatestThrows = new HttpRequestException("offline") };
        var c = await new UpdateOrchestrator(f).CheckAsync(CancellationToken.None);
        Assert.Contains("offline", c.Error);
        Assert.Null(c.Report);
    }

    [Fact(DisplayName = "@important CheckAsync with nothing newer gives an empty outcome")]
    public async Task CheckNothingNewer()
    {
        var f = new FakeOps { Latest = null };
        var c = await new UpdateOrchestrator(f).CheckAsync(CancellationToken.None);
        Assert.Equal(new CheckOutcome(null, null, [], null).Error, c.Error);
        Assert.Null(c.Release);
        Assert.Null(c.Report);
        Assert.Empty(c.Changelog);
    }

    [Theory(DisplayName = "@important CheckAsync names hash, signer and extraction failures")]
    [InlineData("hash", "hash")]
    [InlineData("signer", "signer")]
    [InlineData("extract", "extraction")]
    public async Task CheckNamesFailures(string what, string expected)
    {
        var f = new FakeOps();
        if (what == "hash") f.HashOk = false;
        if (what == "signer") f.SignerOk = false;
        if (what == "extract") f.ExtractThrows = true;
        var c = await new UpdateOrchestrator(f).CheckAsync(CancellationToken.None);
        Assert.Contains(expected, c.Error);
    }

    [Fact(DisplayName = "@important CheckAsync success carries release, report and highlighted changelog")]
    public async Task CheckSuccess()
    {
        var f = new FakeOps();
        var c = await new UpdateOrchestrator(f).CheckAsync(CancellationToken.None);
        Assert.Null(c.Error);
        Assert.Equal(Release, c.Release);
        Assert.True(c.Report!.Compatible);
        Assert.Equal(ChangelogHighlighter.Highlight(Release.Body), c.Changelog);
    }

    [Fact(DisplayName = "@critical a throwing backup installer still restores the backup and names the exception")]
    public async Task BackupInstallerThrowsStillRestores()
    {
        var f = new FakeOps { InstallExit = 1, BackupInstallerThrows = true };
        var o = await Run(f);
        Assert.Equal(UpdateStep.RollbackFailed, o.FailedAt);
        Assert.Contains("RestoreBackup", f.Log);
        Assert.Contains("StartLltAndWaitSelfTest", f.Log.SkipWhile(l => l != "RestoreBackup"));
        Assert.Contains("no saved installer", o.Error);
        Assert.Contains("установщик LLT вернул ошибку", o.Error);
        Assert.Single(f.Notes);
    }

    [Fact(DisplayName = "@critical a failed restore does not skip the rollback self-test and both problems are named")]
    public async Task FailedRestoreStillSelfTests()
    {
        var f = new FakeOps { InstallExit = 1, RestoreThrows = true, RollbackSelfTest = new(false, ["old broken"]) };
        var o = await Run(f);
        Assert.Equal(UpdateStep.RollbackFailed, o.FailedAt);
        Assert.Contains("disk", o.Error);
        Assert.Contains("old broken", o.Error);
    }

    [Fact(DisplayName = "@critical NotifyGuard(true) throwing stops at Install without installing or rolling back")]
    public async Task GuardTrueThrows()
    {
        var f = new FakeOps { GuardTrueThrows = true };
        var o = await Run(f);
        Assert.Equal(UpdateStep.Install, o.FailedAt);
        Assert.Contains("guard pipe", o.Error);
        Assert.DoesNotContain(f.Log, l => l.StartsWith("RunInstaller"));
        Assert.DoesNotContain("RestoreBackup", f.Log);
        Assert.Equal("NotifyGuard(false)", f.Log[^2]);
        Assert.Equal("RecordHistory", f.Log[^1]);
    }

    [Fact(DisplayName = "@critical the first StopLlt throwing stops at Install, restarts LLT and does not roll back")]
    public async Task FirstStopThrows()
    {
        var f = new FakeOps { FirstStopThrows = true };
        var o = await Run(f);
        Assert.Equal(UpdateStep.Install, o.FailedAt);
        Assert.Contains("stop pipe", o.Error);
        Assert.DoesNotContain(f.Log, l => l.StartsWith("RunInstaller"));
        Assert.DoesNotContain("RestoreBackup", f.Log);
        Assert.Contains("StartLltAndWaitLoaded", f.Log);
        Assert.Equal("NotifyGuard(false)", f.Log[^2]);
        Assert.Equal("RecordHistory", f.Log[^1]);
    }

    [Fact(DisplayName = "@important a history line round-trips the full reconciliation report")]
    public void HistoryLineRoundTrips()
    {
        var rec = new ReconcileResult(
            [new FieldChange("a.json", "$.x", "1", "2")], [new FieldChange("b.json", "$.y", null, "3")],
            [new FieldChange("c.json", "$.z", "4", null)], ["k lost"]);
        var line = UpdateHistory.Line(new UpdateOutcome(false, UpdateStep.Reconcile, "e", rec, "bk"), Release, DateTimeOffset.UnixEpoch);
        var r = System.Text.Json.Nodes.JsonNode.Parse(line)!["reconcile"]!;
        Assert.Equal("a.json", (string?)r["restored"]![0]!["File"]);
        Assert.Equal("$.x", (string?)r["restored"]![0]!["JsonPath"]);
        Assert.Equal("1", (string?)r["restored"]![0]!["Before"]);
        Assert.Equal("2", (string?)r["restored"]![0]!["After"]);
        Assert.Equal("3", (string?)r["added"]![0]!["After"]);
        Assert.Equal("4", (string?)r["removed"]![0]!["Before"]);
        Assert.Equal("k lost", (string?)r["keyFailures"]![0]);
    }

    private const string Next = @"C:\Program Files\LegionModes\plugin\2.35.0.0";

    [Fact(DisplayName = "@critical with NextPluginDir set the compat check gets the next plugin and InstallPlugin runs between the installer and the migration start")]
    public async Task PluginSwapOrder()
    {
        var f = new FakeOps { NextDir = Next };
        var check = await new UpdateOrchestrator(f).CheckAsync(CancellationToken.None);
        Assert.Equal(Path.Combine(Next, "LegionModes.dll"), f.CompatPluginDll);
        Assert.True(check.Report!.Compatible);

        f = new FakeOps { NextDir = Next };
        var o = await Run(f);
        Assert.True(o.Success);
        Assert.Equal(Path.Combine(Next, "LegionModes.dll"), f.CompatPluginDll);
        var i = f.Log.IndexOf("InstallPlugin:" + Next);
        Assert.True(i > f.Log.IndexOf("EnforceAfterInstall"));
        Assert.True(i > f.Log.IndexOf("RunInstaller:new.exe"));
        Assert.True(i < f.Log.IndexOf("StartLltAndWaitLoaded"));
    }

    [Fact(DisplayName = "@critical a failing InstallPlugin rolls back and the backup is restored")]
    public async Task PluginSwapFailureRollsBack()
    {
        var f = new FakeOps { NextDir = Next, InstallPluginThrows = true };
        var o = await Run(f);
        Assert.False(o.Success);
        Assert.Equal(UpdateStep.Install, o.FailedAt);
        Assert.Contains("plugin copy", o.Error);
        AssertRolledBack(f);
        Assert.DoesNotContain("StartLltAndWaitLoaded", f.Log);
    }

    [Fact(DisplayName = "@critical a failure after InstallPlugin rolls back and the backup is restored")]
    public async Task FailureAfterPluginSwapRollsBack()
    {
        var f = new FakeOps { NextDir = Next, ReconcileFails = true };
        var o = await Run(f);
        Assert.Equal(UpdateStep.Reconcile, o.FailedAt);
        Assert.Contains("InstallPlugin:" + Next, f.Log);
        AssertRolledBack(f);
    }

    [Fact(DisplayName = "@critical with NextPluginDir null the installed plugin is checked and InstallPlugin never runs")]
    public async Task NoNextPlugin()
    {
        var f = new FakeOps();
        var o = await Run(f);
        Assert.True(o.Success);
        Assert.Equal(LegionModes.Core.Paths.PluginDll, f.CompatPluginDll);
        Assert.DoesNotContain(f.Log, l => l.StartsWith("InstallPlugin"));
    }

    private static ReleaseInfo R(string v) => new(v, "v" + v, "https://x/" + v + ".exe", "abc", "", DateTimeOffset.UnixEpoch);

    [Fact(DisplayName = "@important ReleaseChoice prefers the newest release with a plugin, else the newest, and nothing when not newer")]
    public void ReleaseChoicePicks()
    {
        ReleaseInfo[] rs = [R("2.36.0.0"), R("2.36.4.0"), R("2.36.5.17")];
        Assert.Equal("2.36.4.0", ReleaseChoice.Pick(rs, "2.34.0.0", ["2.36.4.0"])!.Version);
        Assert.Equal("2.36.5.17", ReleaseChoice.Pick(rs, "2.34.0.0", [])!.Version);
        Assert.Equal("2.36.5.17", ReleaseChoice.Pick(rs, "2.36.4.0", ["2.36.4.0"])!.Version);
        Assert.Null(ReleaseChoice.Pick(rs, "2.36.5.17", ["2.36.4.0"]));
    }

    [Fact(DisplayName = "the signer pin is the thumbprint from llt.lock.json")]
    public void SignerPin() => Assert.Equal("5A6C3448B4D2FECBAA7EE1BB592E4A7EEE6FB7A8", Pins.SignerThumbprint);
}
