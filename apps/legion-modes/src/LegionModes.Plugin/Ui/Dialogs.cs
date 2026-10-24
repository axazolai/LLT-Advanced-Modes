using System.Diagnostics;
using System.IO;
using System.Windows;
using LegionModes.Core;
using LegionModes.Plugin.Ui.ViewModels;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows.Dashboard;

namespace LegionModes.Plugin.Ui;

// Dialogs of the page; every call runs on the UI thread, owned by the window that holds the page.
internal sealed class Dialogs(DependencyObject page) : IDialogs
{
    Window? Owner => Window.GetWindow(page);

    public Task<bool> ConfirmAsync(string title, string message, string yes) =>
        MessageBoxHelper.ShowAsync(page, title, message, yes, Tr.T("Cancel"));

    public Task<string?> PromptAsync(string title, string placeholder, string initial) =>
        Owner is { } owner
            ? MessageBoxHelper.ShowInputAsync(owner, title, placeholder, initial, Tr.T("Save"), Tr.T("Cancel"))
            : Task.FromResult<string?>(null);

    public void ShowGodModeWindow() => new GodModeSettingsWindow { Owner = Owner }.ShowDialog();

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
