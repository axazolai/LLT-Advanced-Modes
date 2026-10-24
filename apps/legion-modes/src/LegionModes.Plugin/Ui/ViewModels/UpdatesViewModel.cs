using System.Collections.ObjectModel;
using System.IO;
using LegionModes.Core;
using LegionModes.Core.Ipc;
using LegionModes.Core.Status;

namespace LegionModes.Plugin.Ui.ViewModels;

// One update attempt: version, when, result and what went wrong.
public sealed record HistoryLine(string Version, string When, string Result, Tone Tone, string? Detail);

public sealed class UpdatesViewModel : TabViewModel
{
    UpdatesFile? _file;
    string _installed = Tr.T("no data"), _latest = Tr.T("no data"), _lastCheck = Tr.T("no data"), _compatibility = "", _problems = "";
    Tone _compatibilityTone = Tone.Muted;

    public UpdatesViewModel()
    {
        CheckCommand = new RelayCommand(CheckAsync);
        RefreshCommand = new RelayCommand(RefreshAsync);
    }

    // The keeper's file; the UI stand points it at a sample.
    public static string UpdatesPath { get; set; } = Path.Combine(Paths.DataRoot(), "updates.json");

    public ObservableCollection<ChangelogLine> Changelog { get; } = [];
    public ObservableCollection<HistoryLine> History { get; } = [];
    public RelayCommand CheckCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public string Installed { get => _installed; private set => Set(ref _installed, value); }
    public string Latest { get => _latest; private set => Set(ref _latest, value); }
    public string LastCheck { get => _lastCheck; private set => Set(ref _lastCheck, value); }
    public string Compatibility { get => _compatibility; private set => Set(ref _compatibility, value); }
    public Tone CompatibilityTone { get => _compatibilityTone; private set => Set(ref _compatibilityTone, value); }
    public string Problems { get => _problems; private set { if (Set(ref _problems, value)) Raise(nameof(HasProblems)); } }
    public bool HasProblems => _problems.Length > 0;

    // updates.json is replaced by the keeper; the shared reader returns null while the replace is in flight.
    static async Task<UpdatesFile?> ReadWithRetryAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            var (exists, f) = await Task.Run(() => File.Exists(UpdatesPath) ? (true, UpdatesFile.Load(UpdatesPath)) : (false, null));
            if (!exists) return null;
            if (f is not null) return f;
        }
        return null;
    }

    public async Task RefreshAsync()
    {
        try
        {
            _file = await ReadWithRetryAsync();
            Apply();
        }
        catch (Exception e) { Fail(Tr.T("Could not read updates.json"), e); }
    }

    static string Time(DateTimeOffset? t) => t is null ? Tr.T("no data") : t.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    void Apply()
    {
        Changelog.Clear();
        History.Clear();
        CompatibilityTone = Tone.Muted;
        if (_file is null)
        {
            Installed = Latest = LastCheck = Tr.T("no data");
            Compatibility = "";
            Problems = "";
            Changed?.Invoke();
            return;
        }
        Installed = string.IsNullOrEmpty(_file.InstalledVersion) ? Tr.T("no data") : _file.InstalledVersion;
        LastCheck = Time(_file.LastCheck) + (_file.NextCheck is { } next ? Tr.F(" (next: {0})", Time(next)) : "");
        var last = _file.Last;
        if (last is null)
        {
            Latest = Tr.T("no data");
            Compatibility = "";
            Problems = "";
        }
        else if (last.Error is { } err)
        {
            Latest = Tr.T("check error");
            Compatibility = err;
            CompatibilityTone = Tone.Bad;
            Problems = "";
        }
        else
        {
            Latest = last.Release is { } r ? $"{r.Version} ({r.Published.ToLocalTime():dd.MM.yyyy})" : Tr.T("no data");
            Compatibility = last.Compatible switch
            {
                true => Tr.F("Compatible (references checked: {0})", last.ReferencesChecked),
                false => Tr.F("Incompatible (references checked: {0})", last.ReferencesChecked),
                _ => Tr.T("not checked"),
            };
            CompatibilityTone = last.Compatible switch { true => Tone.Good, false => Tone.Bad, _ => Tone.Muted };
            Problems = string.Join(Environment.NewLine, last.Problems);
            foreach (var line in last.Changelog) Changelog.Add(line);
        }
        foreach (var h in _file.History.OrderByDescending(h => h.At))
            History.Add(h.Success
                ? new HistoryLine(h.Version, Time(h.At), Tr.T("success"), Tone.Good, null)
                : new HistoryLine(h.Version, Time(h.At), h.FailedAt is "Rollback" or "RollbackFailed" ? StepName(h.FailedAt) : Tr.F("failed: {0}", StepName(h.FailedAt)),
                    h.FailedAt == "RollbackFailed" ? Tone.Bad : Tone.Warn, h.Error));
        Changed?.Invoke();
    }

    public event Action? Changed;                      // after every read of updates.json: the tab redraws its cards

    static string StepName(string step) => step switch
    {
        "Download" => Tr.T("download"),
        "Verify" => Tr.T("signature check"),
        "Extract" => Tr.T("unpacking"),
        "Compat" => Tr.T("compatibility check"),
        "Backup" => Tr.T("backup"),
        "Install" => Tr.T("installation"),
        "MigrationStart" => Tr.T("first start"),
        "Reconcile" => Tr.T("settings reconcile"),
        "SelfTest" => Tr.T("self-test"),
        "Rollback" => Tr.T("rolled back"),
        "RollbackFailed" => Tr.T("rollback failed"),
        _ => step,
    };

    async Task Send(string what, Core.Ipc.Message request, TimeSpan timeout)
    {
        Busy = true;
        try
        {
            var reply = await PipeClient.SendAsync(Paths.KeeperPipe, request, timeout);
            if (reply is Ack { Ok: true }) Info(Tr.F("{0}: done", what));
            else if (reply is Ack ack) { IsError = true; Message = Tr.F("{0}: {1}", what, ack.Error); }
            else { IsError = true; Message = Tr.F("{0}: the update keeper (Keeper) is not responding", what); }
        }
        finally
        {
            Busy = false;
        }
    }

    Task CheckAsync() => GuardAsync(Tr.T("Update check"), async () =>
    {
        Info(Tr.T("Check in progress: download and verification can take a few minutes"));
        await Send(Tr.T("Update check"), new CheckRequest(), TimeSpan.FromMinutes(10));   // the keeper replies when the check is done
        await RefreshAsync();
    });

}
