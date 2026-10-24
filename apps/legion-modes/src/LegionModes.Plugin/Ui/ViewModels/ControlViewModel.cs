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

    public string ControlState => _doc.Host.HasControl ? Tr.T("The plugin controls the modes") : Tr.T("LLT has control");
    public Tone ControlTone => _doc.Host.HasControl ? Tone.Good : Tone.Warn;

    // «идёт, осталось 12:30» or «не включена»; the pause itself is a quick action (tray, desktop menu).
    public string PauseState
    {
        get
        {
            var pause = _doc.Host.Last?.Overrides.FirstOrDefault(o => o.Kind == Core.Engine.OverrideKind.Pause);
            if (pause is null) return Tr.T("off|3");
            if (pause.Until is not { } until) return Tr.T("active");
            var left = until - DateTimeOffset.Now;
            return left > TimeSpan.Zero ? Tr.F("active, {0}:{1:00} left", (int)left.TotalMinutes, left.Seconds) : Tr.T("ending");
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
        SnapshotText = at is { } t ? Tr.F("taken {0:dd.MM.yyyy HH:mm}", t) : Tr.T("none: a factory reset will not touch the lighting");
    }

    public string PauseText
    {
        get => _pauseText;
        set { if (Set(ref _pauseText, value)) { Raise(nameof(PauseError)); SavePauseCommand.Refresh(); } }
    }

    public string? PauseError =>
        int.TryParse(_pauseText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 1440
            ? null : Tr.T("Pause length: a whole number of minutes from 1 to 1440");

    Task TakeAsync() => GuardAsync(Tr.T("Could not take control"), async () =>
    {
        if (!await _dialogs.ConfirmAsync(Tr.T("Take control?"), Tr.T("LLT automation will be turned off, LLT power mode binding stopped, and the lid action set to \"do nothing\". The previous values are saved and come back with the \"Return control\" button."),
                Tr.T("Take control"))) return;
        var error = await _doc.Host.TakeControlAsync();
        if (error is null) Info(Tr.T("Control taken"));
        else { IsError = true; Message = error; }
        RefreshState();
    });

    Task ReturnAsync() => GuardAsync(Tr.T("Could not return control"), async () =>
    {
        if (!await Task.Run(() => _doc.Host.ControlHeld)) { IsError = true; Message = Tr.T("control was not taken"); return; }
        if (!await _dialogs.ConfirmAsync(Tr.T("Return control to LLT?"),
                Tr.T("The plugin will stop applying modes; the saved LLT and Windows values will be restored."), Tr.T("Return"))) return;
        var error = await _doc.Host.ReturnControlAsync();
        if (error is null) Info(Tr.T("Control returned"));
        else { IsError = true; Message = error; }
        RefreshState();
    });

    Task SavePauseAsync() => GuardAsync(Tr.T("Could not save the pause"), async () =>
    {
        if (PauseError is not null) return;
        var minutes = int.Parse(_pauseText.Trim(), CultureInfo.InvariantCulture);
        var warn = await _doc.SaveModesAsync(_doc.Modes with { PauseMinutes = minutes });
        Saved(Tr.T("Pause length saved"), warn);
    });

    Task SnapshotAsync() => GuardAsync(Tr.T("Could not capture the lighting"), async () =>
    {
        var path = _doc.Host.LightingSnapshotPath;
        if (await Task.Run(() => File.Exists(path)) && !await _dialogs.ConfirmAsync(Tr.T("A lighting snapshot already exists"),
                Tr.T("Replace it with the current keyboard effects?"), Tr.T("Replace"))) return;
        await LightingStore.SnapshotAsync(path);
        await RefreshSnapshotAsync();
        Info(Tr.T("Lighting snapshot saved"));
    });

    Task FactoryResetAsync() => GuardAsync(Tr.T("Could not reset to factory"), async () =>
    {
        if (!await _dialogs.ConfirmAsync(Tr.T("Reset profiles to factory values?"),
                Tr.T("Profiles and their actions will be replaced with the factory ones, the \"AM · …\" presets get factory values, and lighting effects return from the snapshot if there is one. The program list is not changed."), Tr.T("Reset|2"))) return;
        var warn = await _doc.ResetModesToFactoryAsync();
        var snapshot = _doc.Host.LightingSnapshotPath;
        if (await Task.Run(() => File.Exists(snapshot))) await LightingStore.RestoreAsync(snapshot);
        Saved(Tr.T("Factory values restored"), warn);
    });

    Task ResetProgramsAsync() => GuardAsync(Tr.T("Could not reset the program lists"), async () =>
    {
        if (!await _dialogs.ConfirmAsync(Tr.T("Clear the program list?"), Tr.T("All program entries will be deleted."), Tr.T("Clear"))) return;
        var warn = await _doc.SaveProgramsAsync(new ProgramsConfig(ProgramsConfig.CurrentSchema, []));
        Saved(Tr.T("Program lists cleared"), warn);
    });

    Task OpenLogsAsync() => GuardAsync(Tr.T("Could not open the logs folder"), () =>
    {
        _dialogs.OpenFolder(LogsPath);
        return Task.CompletedTask;
    });
}
