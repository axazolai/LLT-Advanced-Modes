namespace LegionModes.Plugin.Ui.ViewModels;

// What the view-models need from the window they live in. LLT's own message boxes (MessageBoxHelper), Russian buttons.
public interface IDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string yes);
    Task<string?> PromptAsync(string title, string placeholder, string initial);
    void ShowGodModeWindow();                // LLT's GodModeSettingsWindow, modal, on the UI thread
    void OpenFolder(string path);
}
