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
    string _installed = Tr.T("нет данных"), _latest = Tr.T("нет данных"), _lastCheck = Tr.T("нет данных"), _compatibility = "", _problems = "";
    Tone _compatibilityTone = Tone.Muted;

    public UpdatesViewModel()
    {
        CheckCommand = new RelayCommand(CheckAsync);
        UpdateCommand = new RelayCommand(UpdateAsync, () => CanUpdate);
        RefreshCommand = new RelayCommand(RefreshAsync);
    }

    // The keeper's file; the UI stand points it at a sample.
    public static string UpdatesPath { get; set; } = Path.Combine(Paths.DataRoot(), "updates.json");

    public ObservableCollection<ChangelogLine> Changelog { get; } = [];
    public ObservableCollection<HistoryLine> History { get; } = [];
    public RelayCommand CheckCommand { get; }
    public RelayCommand UpdateCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public string Installed { get => _installed; private set => Set(ref _installed, value); }
    public string Latest { get => _latest; private set => Set(ref _latest, value); }
    public string LastCheck { get => _lastCheck; private set => Set(ref _lastCheck, value); }
    public string Compatibility { get => _compatibility; private set => Set(ref _compatibility, value); }
    public Tone CompatibilityTone { get => _compatibilityTone; private set => Set(ref _compatibilityTone, value); }
    public string Problems { get => _problems; private set { if (Set(ref _problems, value)) Raise(nameof(HasProblems)); } }
    public bool HasProblems => _problems.Length > 0;

    public bool CanUpdate => _file?.Last is { Compatible: true, Release: { } r } && r.Version != _file.InstalledVersion && !Busy;

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
        catch (Exception e) { Fail(Tr.T("Не удалось прочитать updates.json"), e); }
        UpdateCommand.Refresh();
    }

    static string Time(DateTimeOffset? t) => t is null ? Tr.T("нет данных") : t.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    void Apply()
    {
        Changelog.Clear();
        History.Clear();
        CompatibilityTone = Tone.Muted;
        if (_file is null)
        {
            Installed = Latest = LastCheck = Tr.T("нет данных");
            Compatibility = "";
            Problems = "";
            Raise(nameof(CanUpdate));
            Changed?.Invoke();
            return;
        }
        Installed = string.IsNullOrEmpty(_file.InstalledVersion) ? Tr.T("нет данных") : _file.InstalledVersion;
        LastCheck = Time(_file.LastCheck) + (_file.NextCheck is { } next ? Tr.F(" (следующая: {0})", Time(next)) : "");
        var last = _file.Last;
        if (last is null)
        {
            Latest = Tr.T("нет данных");
            Compatibility = "";
            Problems = "";
        }
        else if (last.Error is { } err)
        {
            Latest = Tr.T("ошибка проверки");
            Compatibility = err;
            CompatibilityTone = Tone.Bad;
            Problems = "";
        }
        else
        {
            Latest = last.Release is { } r ? $"{r.Version} ({r.Published.ToLocalTime():dd.MM.yyyy})" : Tr.T("нет данных");
            Compatibility = last.Compatible switch
            {
                true => Tr.F("Совместимо (проверено ссылок: {0})", last.ReferencesChecked),
                false => Tr.F("Несовместимо (проверено ссылок: {0})", last.ReferencesChecked),
                _ => Tr.T("не проверялось"),
            };
            CompatibilityTone = last.Compatible switch { true => Tone.Good, false => Tone.Bad, _ => Tone.Muted };
            Problems = string.Join(Environment.NewLine, last.Problems);
            foreach (var line in last.Changelog) Changelog.Add(line);
        }
        foreach (var h in _file.History.OrderByDescending(h => h.At))
            History.Add(h.Success
                ? new HistoryLine(h.Version, Time(h.At), Tr.T("успешно"), Tone.Good, null)
                : new HistoryLine(h.Version, Time(h.At), h.FailedAt is "Rollback" or "RollbackFailed" ? StepName(h.FailedAt) : Tr.F("сбой: {0}", StepName(h.FailedAt)),
                    h.FailedAt == "RollbackFailed" ? Tone.Bad : Tone.Warn, h.Error));
        Changed?.Invoke();
        Raise(nameof(CanUpdate));
    }

    public event Action? Changed;                      // after every read of updates.json: the tab redraws its cards

    static string StepName(string step) => step switch
    {
        "Download" => Tr.T("скачивание"),
        "Verify" => Tr.T("проверка подписи"),
        "Extract" => Tr.T("распаковка"),
        "Compat" => Tr.T("проверка совместимости"),
        "Backup" => Tr.T("резервная копия"),
        "Install" => Tr.T("установка"),
        "MigrationStart" => Tr.T("первый запуск"),
        "Reconcile" => Tr.T("сверка настроек"),
        "SelfTest" => Tr.T("самопроверка"),
        "Rollback" => Tr.T("откат выполнен"),
        "RollbackFailed" => Tr.T("откат не удался"),
        _ => step,
    };

    async Task Send(string what, Core.Ipc.Message request, TimeSpan timeout)
    {
        Busy = true;
        UpdateCommand.Refresh();
        try
        {
            var reply = await PipeClient.SendAsync(Paths.KeeperPipe, request, timeout);
            if (reply is Ack { Ok: true }) Info(Tr.F("{0}: готово", what));
            else if (reply is Ack ack) { IsError = true; Message = Tr.F("{0}: {1}", what, ack.Error); }
            else { IsError = true; Message = Tr.F("{0}: сторож обновлений (Keeper) не отвечает", what); }
        }
        finally
        {
            Busy = false;
            Raise(nameof(CanUpdate));
            UpdateCommand.Refresh();
        }
    }

    Task CheckAsync() => GuardAsync(Tr.T("Проверка обновлений"), async () =>
    {
        Info(Tr.T("Проверка идёт: скачивание и сверка могут занять несколько минут"));
        await Send(Tr.T("Проверка обновлений"), new CheckRequest(), TimeSpan.FromMinutes(10));   // the keeper replies when the check is done
        await RefreshAsync();
    });

    Task UpdateAsync() => GuardAsync(Tr.T("Обновление"), async () =>
    {
        if (_file?.Last?.Release is not { } r) return;
        await Send(Tr.T("Обновление"), new UpdateRequest(r.Version), TimeSpan.FromSeconds(15));
    });
}
