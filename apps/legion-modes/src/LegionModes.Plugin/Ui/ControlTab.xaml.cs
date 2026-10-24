using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LegionModes.Core;
using LegionModes.Plugin.Ui.ViewModels;
using Wpf.Ui.Common;
using static LegionModes.Plugin.Ui.Look;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using WpfUiButton = Wpf.Ui.Controls.Button;
using WpfUiTextBox = Wpf.Ui.Controls.TextBox;

namespace LegionModes.Plugin.Ui;

// Управление: take and return control, the pause length, the lighting snapshot, the resets, the logs. Built once; the
// values are bound to the view model.
public partial class ControlTab : UserControl
{
    public ControlTab()
    {
        Resources.MergedDictionaries.Add(SharedResources.Instance);   // before InitializeComponent: StaticResource is resolved while loading
        InitializeComponent();
    }

    public void Bind(ControlViewModel vm)
    {
        DataContext = vm;

        var state = Text("", "MainValue");
        state.SetBinding(TextBlock.TextProperty, new Binding(nameof(ControlViewModel.ControlState)));
        void Tone() => state.SetResourceReference(TextBlock.ForegroundProperty, vm.ControlTone == ViewModels.Tone.Good ? "PaletteGreenBrush" : "PaletteOrangeBrush");
        Tone();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ControlViewModel.ControlTone)) Tone(); };
        var control = new StackPanel();
        control.Children.Add(state);
        control.Children.Add(Text(Tr.T("Take: LLT automation and LLT's power mode binding are turned off, the lid action becomes \"do nothing\"; the previous values are saved. Return: the plugin stops applying modes, the saved values come back."),
                                  "SubLine", new Thickness(0, 4, 0, 12)));
        control.Children.Add(Buttons(
            new WpfUiButton { Content = Tr.T("Take control"), Icon = SymbolRegular.Play24, Appearance = ControlAppearance.Primary, Command = vm.TakeCommand },
            new WpfUiButton { Content = Tr.T("Return control"), Icon = SymbolRegular.ArrowUndo24, Command = vm.ReturnCommand }));
        _left.Children.Add(Card(SymbolRegular.Settings24, "PaletteLightBlueBrush", Tr.T("Mode control"), control));

        var pause = new StackPanel();
        pause.Children.Add(Bound(SymbolRegular.PauseCircle24, Tr.T("Now"), nameof(ControlViewModel.PauseState)));
        var minutes = new WpfUiTextBox { Width = 100 };
        minutes.SetBinding(System.Windows.Controls.TextBox.TextProperty, new Binding(nameof(ControlViewModel.PauseText)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(minutes);
        row.Children.Add(new WpfUiButton { Content = Tr.T("Save"), Command = vm.SavePauseCommand, Margin = new Thickness(8, 0, 0, 0) });
        pause.Children.Add(Row(SymbolRegular.Timer24, Tr.T("Duration, minutes (1–1440)"), row));
        var error = Text("", "ErrorText");
        error.SetBinding(TextBlock.TextProperty, new Binding(nameof(ControlViewModel.PauseError)));
        void ShowError() => error.Visibility = vm.PauseError is null ? Visibility.Collapsed : Visibility.Visible;
        ShowError();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ControlViewModel.PauseError)) ShowError(); };
        pause.Children.Add(error);
        pause.Children.Add(Text(Tr.T("Pause is switched on in the tray or the desktop menu; while it lasts, parameters are not applied."), "SubLine"));
        _left.Children.Add(Card(SymbolRegular.PauseCircle24, "PaletteOrangeBrush", Tr.T("Pause"), pause));

        var lighting = new StackPanel();
        lighting.Children.Add(Bound(SymbolRegular.Camera24, Tr.T("Snapshot"), nameof(ControlViewModel.SnapshotText)));
        lighting.Children.Add(Text(Tr.T("The effects of the six Spectrum profiles that the factory reset restores."), "SubLine", new Thickness(0, 0, 0, 12)));
        lighting.Children.Add(Buttons(new WpfUiButton { Content = Tr.T("Take snapshot"), Icon = SymbolRegular.Camera24, Command = vm.SnapshotCommand }));
        _right.Children.Add(Card(SymbolRegular.Keyboard24, "PalettePurpleBrush", Tr.T("Keyboard lighting"), lighting));

        var reset = new StackPanel();
        reset.Children.Add(Text(Tr.T("Profiles to factory: profiles and their actions, \"AM · …\" presets and the lighting from the snapshot. Program list clear: all entries are removed."),
                                "SubLine", new Thickness(0, 0, 0, 12)));
        reset.Children.Add(Buttons(
            new WpfUiButton { Content = Tr.T("Profiles to factory"), Icon = SymbolRegular.ArrowReset24, Command = vm.FactoryResetCommand },
            new WpfUiButton { Content = Tr.T("Clear the program list"), Icon = SymbolRegular.Delete24, Command = vm.ResetProgramsCommand }));
        _right.Children.Add(Card(SymbolRegular.ArrowReset24, "PaletteRedBrush", Tr.T("Reset"), reset));

        var logs = new StackPanel();
        logs.Children.Add(Row(SymbolRegular.Folder24, vm.LogsPath, new WpfUiButton { Content = Tr.T("Open folder"), Icon = SymbolRegular.FolderOpen24, Command = vm.OpenLogsCommand }));
        _right.Children.Add(Card(SymbolRegular.DocumentBulletList24, "PaletteBlueBrush", Tr.T("Logs"), logs));

        _ = vm.RefreshSnapshotAsync();
    }

    static UIElement Buttons(params WpfUiButton[] buttons)
    {
        var row = new WrapPanel();
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(b);
        }
        return row;
    }

    // "Label: value" bound to a view-model property.
    static UIElement Bound(SymbolRegular icon, string label, string property)
    {
        var value = Text("", "RowValue");
        value.SetBinding(TextBlock.TextProperty, new Binding(property));
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var i = new Wpf.Ui.Controls.SymbolIcon { Symbol = icon };
        i.SetResourceReference(StyleProperty, "RowIcon");
        panel.Children.Add(i);
        panel.Children.Add(Text(label + ":", "RowLabel", new Thickness(0, 0, 4, 0)));
        panel.Children.Add(value);
        return panel;
    }
}
