using System.Windows.Input;

namespace LegionModes.Plugin.Ui.ViewModels;

// Shared by the tabs: a message line (result or error) and a command that catches everything.
public abstract class TabViewModel : ViewModelBase
{
    string _message = "";
    bool _isError;
    bool _busy;

    public string Message { get => _message; protected set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;
    public bool IsError { get => _isError; protected set => Set(ref _isError, value); }
    public bool Busy { get => _busy; protected set => Set(ref _busy, value); }

    protected void Info(string text) { IsError = false; Message = text; }

    // After a save: the result line, or the note about a tab that did not refresh.
    protected void Saved(string ok, string? warn)
    {
        IsError = warn is not null;
        Message = warn ?? ok;
    }

    protected void Fail(string what, Exception e)
    {
        Log.Error(what, e);
        IsError = true;
        Message = $"{what}: {e.Message}";
    }

    // Runs `action` on the calling (UI) thread; nothing escapes into LLT.
    protected async Task GuardAsync(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { Fail(what, e); }
    }
}

public sealed class RelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        catch (Exception e)
        {
            Log.Error("command", e);
            try { onError?.Invoke(e); } catch (Exception inner) { Log.Error("command error handler", inner); }
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
