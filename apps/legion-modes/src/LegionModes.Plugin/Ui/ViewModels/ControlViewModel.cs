using System.Globalization;
using System.IO;
using LegionModes.Core;
using LegionModes.Core.Model;
using LegionModes.Core.Programs;
using LegionModes.Plugin.Backlight;

namespace LegionModes.Plugin.Ui.ViewModels;

public sealed class ControlViewModel : TabViewModel
{
    readonly ModesDocument _doc;
    readonly IDialogs _dialogs;
    string _pauseText;

    public ControlViewModel(ModesDocument doc, IDialogs dialogs)
    {
        _doc = doc;
        _dialogs = dialogs;
        _pauseText = doc.Modes.PauseMinutes.ToString(CultureInfo.InvariantCulture);
        TakeCommand = new RelayCommand(TakeAsync);
        ReturnCommand = new RelayCommand(ReturnAsync);
        SavePauseCommand = new RelayCommand(SavePauseAsync, () => PauseError is null);
        SnapshotCommand = new RelayCommand(SnapshotAsync);
        FactoryResetCommand = new RelayCommand(FactoryResetAsync);
        ResetProgramsCommand = new RelayCommand(ResetProgramsAsync);
        OpenLogsCommand = new RelayCommand(OpenLogsAsync);
        // A modes save elsewhere shows its pause length, unless the field holds an unsaved edit.
        var shown = _pauseText;
        doc.ModesChanged += () =>
        {
            var saved = _doc.Modes.PauseMinutes.ToString(CultureInfo.InvariantCulture);
            var edited = _pauseText != shown && _pauseText != saved;
            shown = saved;
            if (edited) return;
            _pauseText = saved;
            Raise(nameof(PauseText));
            Raise(nameof(PauseError));
        };
    }

    public RelayCommand TakeCommand { get; }
    public RelayCommand ReturnCommand { get; }
    public RelayCommand SavePauseCommand { get; }
    public RelayCommand SnapshotCommand { get; }
    public RelayCommand FactoryResetCommand { get; }
    public RelayCommand ResetProgramsCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    // Called on every status change and by the page's one-second timer (the pause countdown).
    public void RefreshState()
    {
        Raise(nameof(ControlState));
        Raise(nameof(ControlTone));
        Raise(nameof(PauseState));
    }

    public string ControlState => _doc.Host.HasControl ? Tr.T("Плагин управляет режимами") : Tr.T("Управление у LLT");
    public Tone ControlTone => _doc.Host.HasControl ? Tone.Good : Tone.Warn;

    // «идёт, осталось 12:30» or «не включена»; the pause itself is a quick action (tray, desktop menu).
    public string PauseState
    {
        get
        {
            var pause = _doc.Host.Last?.Overrides.FirstOrDefault(o => o.Kind == Core.Engine.OverrideKind.Pause);
            if (pause is null) return Tr.T("не включена");
            if (pause.Until is not { } until) return Tr.T("идёт");
            var left = until - DateTimeOffset.Now;
            return left > TimeSpan.Zero ? Tr.F("идёт, осталось {0}:{1:00}", (int)left.TotalMinutes, left.Seconds) : Tr.T("заканчивается");
        }
    }

    string _snapshotText = "";
    public string SnapshotText { get => _snapshotText; private set => Set(ref _snapshotText, value); }

    public string LogsPath => Path.Combine(Paths.LltDataDir, "log");

    // «снят 20.10.2026 14:32» or «нет»: read off the UI thread.
    public async Task RefreshSnapshotAsync()
    {
        var path = _doc.Host.LightingSnapshotPath;
        var at = await Task.Run(() => File.Exists(path) ? File.GetLastWriteTime(path) : (DateTime?)null);
        SnapshotText = at is { } t ? Tr.F("снят {0:dd.MM.yyyy HH:mm}", t) : Tr.T("нет: сброс к заводским не тронет подсветку");
    }

    public string PauseText
    {
        get => _pauseText;
        set { if (Set(ref _pauseText, value)) { Raise(nameof(PauseError)); SavePauseCommand.Refresh(); } }
    }

    public string? PauseError =>
        int.TryParse(_pauseText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 1440
            ? null : Tr.T("Длительность паузы: целое число минут от 1 до 1440");

    Task TakeAsync() => GuardAsync(Tr.T("Не удалось взять управление"), async () =>
    {
        if (!await _dialogs.ConfirmAsync(Tr.T("Взять управление?"), Tr.T("Автоматизация LLT будет выключена, привязка режимов питания LLT остановлена, " +
                "действие крышки станет «ничего не делать». Прежние значения сохраняются и вернутся кнопкой «Вернуть управление»."),
                Tr.T("Взять управление"))) return;
        var error = await _doc.Host.TakeControlAsync();
        if (error is null) Info(Tr.T("Управление взято"));
        else { IsError = true; Message = error; }
        RefreshState();
    });

    Task ReturnAsync() => GuardAsync(Tr.T("Не удалось вернуть управление"), async () =>
    {
        if (!await Task.Run(() => _doc.Host.ControlHeld)) { IsError = true; Message = Tr.T("управление не было взято"); return; }
        if (!await _dialogs.ConfirmAsync(Tr.T("Вернуть управление LLT?"),
                Tr.T("Плагин перестанет применять режимы, сохранённые значения LLT и Windows будут восстановлены."), Tr.T("Вернуть"))) return;
        var error = await _doc.Host.ReturnControlAsync();
        if (error is null) Info(Tr.T("Управление возвращено"));
        else { IsError = true; Message = error; }
        RefreshState();
    });

    Task SavePauseAsync() => GuardAsync(Tr.T("Не удалось сохранить паузу"), async () =>
    {
        if (PauseError is not null) return;
        var minutes = int.Parse(_pauseText.Trim(), CultureInfo.InvariantCulture);
        var warn = await _doc.SaveModesAsync(_doc.Modes with { PauseMinutes = minutes });
        Saved(Tr.T("Длительность паузы сохранена"), warn);
    });

    Task SnapshotAsync() => GuardAsync(Tr.T("Не удалось снять подсветку"), async () =>
    {
        var path = _doc.Host.LightingSnapshotPath;
        if (await Task.Run(() => File.Exists(path)) && !await _dialogs.ConfirmAsync(Tr.T("Снимок подсветки уже есть"),
                Tr.T("Заменить его текущими эффектами клавиатуры?"), Tr.T("Заменить"))) return;
        await LightingStore.SnapshotAsync(path);
        await RefreshSnapshotAsync();
        Info(Tr.T("Снимок подсветки сохранён"));
    });

    Task FactoryResetAsync() => GuardAsync(Tr.T("Не удалось сбросить к заводским"), async () =>
    {
        if (!await _dialogs.ConfirmAsync(Tr.T("Сбросить профили к заводским значениям?"),
                Tr.T("Профили и их действия будут заменены заводскими, пресеты «AM · …» получат заводские значения, " +
                "эффекты подсветки вернутся из снимка, если он есть. Список программ не изменится."), Tr.T("Сбросить"))) return;
        var warn = await _doc.ResetModesToFactoryAsync();
        var snapshot = _doc.Host.LightingSnapshotPath;
        if (await Task.Run(() => File.Exists(snapshot))) await LightingStore.RestoreAsync(snapshot);
        Saved(Tr.T("Заводские значения восстановлены"), warn);
    });

    Task ResetProgramsAsync() => GuardAsync(Tr.T("Не удалось сбросить списки программ"), async () =>
    {
        if (!await _dialogs.ConfirmAsync(Tr.T("Очистить список программ?"), Tr.T("Все записи о программах будут удалены."), Tr.T("Очистить"))) return;
        var warn = await _doc.SaveProgramsAsync(new ProgramsConfig(ProgramsConfig.CurrentSchema, []));
        Saved(Tr.T("Списки программ очищены"), warn);
    });

    Task OpenLogsAsync() => GuardAsync(Tr.T("Не удалось открыть папку логов"), () =>
    {
        _dialogs.OpenFolder(LogsPath);
        return Task.CompletedTask;
    });
}
