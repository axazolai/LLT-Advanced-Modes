using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using LegionModes.Core;
using LegionModes.Core.Contracts;
using LegionModes.Core.Ipc;
using LegionModes.Core.Status;
using LegionModes.Updates;

namespace LegionModes.Keeper;

// The operating-system side of the update flow; every decision is made in UpdateOrchestrator.
internal sealed class UpdateOps(ReleaseClient client, HttpClient http, KeeperState state, Action<string> notify) : IUpdateOps
{
    const string AutorunTask = "LenovoLegionToolkit_Autorun_6efcc882-924c-4cbc-8fec-f45c25696f98";
    const string ProgramFilesDir = @"C:\Program Files\LenovoLegionToolkit";
    const string InstallerArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
    const int KeptBackups = 3;
    static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(15);

    static string Root => Paths.DataRoot();
    static string BackupRoot => Path.Combine(Root, "backup");
    static string PluginDir => Path.Combine(Paths.LltDataDir, "Plugins", Paths.PluginId);

    volatile TaskCompletionSource<bool>? _loaded;
    volatile TaskCompletionSource<SelfTestReport>? _selfTest;
    volatile string? _currentBackup;

    public void OnLoaded() => _loaded?.TrySetResult(true);

    public void OnSelfTest(SelfTestReport report) => _selfTest?.TrySetResult(report);

    public static string InnounpPath()
    {
#if DEBUG
        // Smoke runs only: a Release keeper runs elevated and must not take a tool path from its environment.
        var overridden = Environment.GetEnvironmentVariable("LEGIONMODES_INNOUNP");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
#endif
        return Path.Combine(AppContext.BaseDirectory, "innounp", "innounp.exe");
    }

    public string InstalledVersion() => ReadInstalledVersion();

    public static string ReadInstalledVersion()
    {
        try
        {
            if (!File.Exists(Paths.LltExe)) return "0.0.0.0";
            var v = FileVersionInfo.GetVersionInfo(Paths.LltExe);
            return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}";
        }
        catch { return "0.0.0.0"; }
    }

    public Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct) =>
        client.LatestAsync(InstalledVersion(), PluginVersions(), ct);

    static string InstallerPath(ReleaseInfo r) =>
        Path.Combine(Root, "installers", r.Version, Path.GetFileName(new Uri(r.AssetUrl).LocalPath));

    public async Task<string> DownloadAsync(ReleaseInfo r, CancellationToken ct)
    {
        var path = InstallerPath(r);
        if (File.Exists(path) && VerifyHash(path, r.AssetSha256)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        using (var response = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(part);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }
        File.Move(part, path, overwrite: true);
        return path;
    }

    public bool VerifyHash(string installer, string sha256)
    {
        using var s = File.OpenRead(installer);
        return string.Equals(Convert.ToHexString(SHA256.HashData(s)), sha256, StringComparison.OrdinalIgnoreCase);
    }

    public bool VerifySigner(string installer, string thumbprint) => Authenticode.SignerMatches(installer, thumbprint);

    public string Extract(string installer)
    {
        var innounp = InnounpPath();
        if (!File.Exists(innounp)) throw new FileNotFoundException(Tr.T("innounp не найден"), innounp);
        var extractRoot = Path.Combine(Root, "extract");
        if (Directory.Exists(extractRoot))
            foreach (var old in Directory.GetDirectories(extractRoot)) Directory.Delete(old, recursive: true);   // earlier checks' leftovers
        var dir = Path.Combine(extractRoot, Path.GetFileName(Path.GetDirectoryName(installer)!));
        Directory.CreateDirectory(dir);
        var (code, output) = Run(innounp, ["-x", "-b", "-q", "-y", "-o", "-d" + dir, installer], TimeSpan.FromMinutes(5));
        var app = Path.Combine(dir, "{app}");
        if (code != 0 || !Directory.Exists(app)) throw new InvalidOperationException(Tr.F("innounp вернул код {0}: {1}", code, output).Trim());
        return app;
    }

    public CompatibilityReport CheckCompat(string hostDir, string pluginDll)
    {
        if (!File.Exists(pluginDll))
            return new CompatibilityReport(false, [$"plugin assembly not found: {pluginDll}"], 0);
        return CompatibilityChecker.Check(pluginDll, hostDir,
            CompatibilityChecker.HostAssembliesOf(pluginDll, hostDir), ReflectionContracts.All);
    }

    static string PluginsRoot => Path.Combine(Paths.InstallDir, "plugin");

    public string? NextPluginDir(string version)
    {
        var dir = Path.Combine(PluginsRoot, version);
        return File.Exists(Path.Combine(dir, Paths.PluginId + ".dll")) ? dir : null;
    }

    public IReadOnlyList<string> PluginVersions() =>
        Directory.Exists(PluginsRoot)
            ? [.. Directory.GetDirectories(PluginsRoot).Where(d => File.Exists(Path.Combine(d, Paths.PluginId + ".dll"))).Select(d => Path.GetFileName(d))]
            : [];

    // The plugin folder of this install that the swap wrote; Reconcile compares the live folder against it.
    volatile string? _swappedFrom;

    public void InstallPlugin(string pluginDir)
    {
        FileMirror.Mirror(pluginDir, PluginDir);
        _swappedFrom = pluginDir;
        KeeperLog.Write($"plugin installed from {pluginDir}");
    }

    public string Backup(string installedInstaller)
    {
        if (!File.Exists(installedInstaller)) throw new FileNotFoundException(Tr.T("нет установщика установленной версии"), installedInstaller);
        var dir = Path.Combine(BackupRoot, $"{DateTime.Now:yyyyMMdd-HHmm}-{InstalledVersion()}");
        for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(BackupRoot, $"{DateTime.Now:yyyyMMdd-HHmm}-{InstalledVersion()}_{n}");
        try
        {
            FileMirror.Mirror(ProgramFilesDir, Path.Combine(dir, "app"));
            FileMirror.Mirror(Paths.LltDataDir, Path.Combine(dir, "data"), excludeTop: "log");
            var installerDir = Path.Combine(dir, "installer");
            Directory.CreateDirectory(installerDir);
            File.Copy(installedInstaller, Path.Combine(installerDir, Path.GetFileName(installedInstaller)), overwrite: true);
        }
        catch
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* partial backup stays out of the list */ }
            throw;
        }
        _currentBackup = dir;
        _swappedFrom = null;
        Prune();
        KeeperLog.Write("backup written: " + dir);
        return dir;
    }

    static void Prune()
    {
        foreach (var old in Directory.GetDirectories(BackupRoot).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(KeptBackups))
        {
            try { Directory.Delete(old, recursive: true); }
            catch (Exception e) { KeeperLog.Write($"cannot prune {old}: {e.Message}"); }
        }
    }

    public Task NotifyGuardAsync(bool updateActive) =>
        PipeClient.SendAsync(Paths.GuardPipe, new UpdateInProgress(updateActive), TimeSpan.FromSeconds(3));

    public async Task<bool> StopLltGracefullyAsync(TimeSpan timeout)
    {
        if (LltSupervisor.Find() is not { } first) return true;
        first.Dispose();
        // No reply is not fatal: the plugin may be busy or absent; the wait below decides.
        await PipeClient.SendAsync(Paths.PluginPipe, new ExitRequest(), TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (LltSupervisor.Find() is { } p) p.Dispose(); else return true;
            await Task.Delay(500);
        }
        var left = LltSupervisor.Find();
        left?.Dispose();
        return left is null;
    }

    public int RunInstaller(string installer)
    {
        // Only installers kept under the data root (a protected folder) run elevated.
        if (!DataRootSecurity.IsUnder(Root, installer))
            throw new InvalidOperationException(Tr.F("установщик {0} вне корня данных {1}; отказано", installer, Root));
        using var p = Process.Start(new ProcessStartInfo(installer, InstallerArgs) { UseShellExecute = false })
                      ?? throw new InvalidOperationException(Tr.T("установщик не запустился"));
        if (p.WaitForExit(InstallerTimeout)) return p.ExitCode;
        try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        KeeperLog.Write($"installer {installer} did not finish in {InstallerTimeout}; killed");
        return -1;
    }

    public async Task<bool> StartLltAndWaitLoadedAsync(TimeSpan timeout)
    {
        var tcs = _loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Always waits for Loaded; an LLT that is already running (e.g. started by the installer) does not short-circuit it.
        if (LltSupervisor.Find() is { } running) running.Dispose();
        else
        {
            using var started = LltSupervisor.Start();
            if (started is null) return false;
        }
        return await Task.WhenAny(tcs.Task, Task.Delay(timeout)) == tcs.Task;
    }

    public async Task<SelfTestReport?> StartLltAndWaitSelfTestAsync(TimeSpan timeout)
    {
        var tcs = _selfTest = new TaskCompletionSource<SelfTestReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (LltSupervisor.Find() is { } running) running.Dispose();
        else
        {
            using var started = LltSupervisor.Start();
            if (started is null) return null;
        }
        return await Task.WhenAny(tcs.Task, Task.Delay(timeout)) == tcs.Task ? tcs.Task.Result : null;
    }

    public ReconcileResult Reconcile(string backupDir, bool hasControl)
    {
        var result = SettingsReconciler.Reconcile(Path.Combine(backupDir, "data"), Paths.LltDataDir, KeySettings.For(hasControl));
        var from = _swappedFrom;
        if (from is null) return result;
        var differs = PluginFilesDiffer(from, PluginDir);
        return differs.Count == 0 ? result : result with { KeyFailures = [.. result.KeyFailures, .. differs.Select(f => Tr.F("файл плагина отличается от пакета: {0}", f))] };
    }

    // Relative names of files of `expected` that are missing or different under `live`.
    static List<string> PluginFilesDiffer(string expected, string live)
    {
        var diffs = new List<string>();
        var want = Directory.Exists(expected) ? Directory.GetFiles(expected, "*", SearchOption.AllDirectories) : [];
        foreach (var f in want)
        {
            var rel = Path.GetRelativePath(expected, f);
            var other = Path.Combine(live, rel);
            if (!File.Exists(other) || !File.ReadAllBytes(f).AsSpan().SequenceEqual(File.ReadAllBytes(other))) diffs.Add(rel);
        }
        return diffs;
    }

    public void EnforceAfterInstall()
    {
        var (queryCode, _) = Run("schtasks.exe", ["/Query", "/TN", AutorunTask], TimeSpan.FromSeconds(30));
        if (queryCode == 0)
        {
            var (deleteCode, output) = Run("schtasks.exe", ["/Delete", "/TN", AutorunTask, "/F"], TimeSpan.FromSeconds(30));
            KeeperLog.Write($"LLT autorun task deleted (exit {deleteCode}) {output}".Trim());
        }

        var settingsPath = Path.Combine(Paths.LltDataDir, "update_settings.json");
        var node = (File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject : null) ?? new JsonObject();
        node["UpdateCheckFrequency"] = "Never";
        Directory.CreateDirectory(Paths.LltDataDir);
        File.WriteAllText(settingsPath, node.ToJsonString(new() { WriteIndented = true }));

        if (!File.Exists(Path.Combine(PluginDir, Paths.PluginId + ".dll")))
        {
            var backup = _currentBackup;
            var saved = backup is null ? null : Path.Combine(backup, "data", "Plugins", Paths.PluginId);
            if (saved is not null && Directory.Exists(saved))
            {
                FileMirror.Mirror(saved, PluginDir);
                KeeperLog.Write("plugin folder was missing after the install; copied back from the backup");
            }
            else KeeperLog.Write("plugin folder is missing after the install and there is no backup to copy it from");
        }
    }

    public bool HasControl()
    {
        try
        {
            var path = Path.Combine(Paths.LltDataDir, "Plugins", "Configs", Paths.PluginId, "control.json");
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj) return false;
            foreach (var (key, value) in obj)
                if (string.Equals(key, "hasControl", StringComparison.OrdinalIgnoreCase)) return value?.GetValue<bool>() ?? false;
            return false;
        }
        catch { return false; }
    }

    public void RestoreBackup(string backupDir)
    {
        FileMirror.Mirror(Path.Combine(backupDir, "app"), ProgramFilesDir);
        FileMirror.Mirror(Path.Combine(backupDir, "data"), Paths.LltDataDir, excludeTop: "log");
    }

    public string InstallerOf(string backupDir)
    {
        var dir = Path.Combine(backupDir, "installer");
        return Directory.GetFiles(dir, "*.exe").FirstOrDefault() ?? throw new FileNotFoundException(Tr.T("в копии нет установщика"), dir);
    }

    public void RecordHistory(UpdateOutcome outcome, ReleaseInfo release)
    {
        var at = DateTimeOffset.Now;
        try { UpdateHistory.Append(Path.Combine(Root, "update-history.jsonl"), outcome, release, at); }
        catch (Exception e) { KeeperLog.Write("history line failed: " + e.Message); }
        KeeperLog.Write($"update {release.Version}: success={outcome.Success} failedAt={outcome.FailedAt} error={outcome.Error}");
        if (outcome.Success) state.InstalledInstaller = InstallerPath(release);
        state.Update(u => u with
        {
            InstalledVersion = InstalledVersion(),
            History = [.. u.History, new HistoryEntry(at, release.Version, release.Tag, outcome.Success, outcome.FailedAt.ToString(), outcome.Error, outcome.BackupDir)],
        });
    }

    public void Notify(string text) => notify(text);

    static (int Code, string Output) Run(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Tr.F("{0} не запустился", exe));
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "timed out");
        }
        return (p.ExitCode, (output.Result + error.Result).Trim());
    }
}
