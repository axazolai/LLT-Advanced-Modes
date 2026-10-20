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
        control.Children.Add(Text(Tr.T("Взять: выключаются автоматизация LLT и привязка режимов питания LLT, действие крышки становится «ничего не делать»; " +
                                  "прежние значения сохраняются. Вернуть: плагин перестаёт применять режимы, сохранённое возвращается."),
                                  "SubLine", new Thickness(0, 4, 0, 12)));
        control.Children.Add(Buttons(
            new WpfUiButton { Content = Tr.T("Взять управление"), Icon = SymbolRegular.Play24, Appearance = ControlAppearance.Primary, Command = vm.TakeCommand },
            new WpfUiButton { Content = Tr.T("Вернуть управление"), Icon = SymbolRegular.ArrowUndo24, Command = vm.ReturnCommand }));
        _left.Children.Add(Card(SymbolRegular.Settings24, "PaletteLightBlueBrush", Tr.T("Управление режимами"), control));

        var pause = new StackPanel();
        pause.Children.Add(Bound(SymbolRegular.PauseCircle24, Tr.T("Сейчас"), nameof(ControlViewModel.PauseState)));
        var minutes = new WpfUiTextBox { Width = 100 };
        minutes.SetBinding(System.Windows.Controls.TextBox.TextProperty, new Binding(nameof(ControlViewModel.PauseText)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(minutes);
        row.Children.Add(new WpfUiButton { Content = Tr.T("Сохранить"), Command = vm.SavePauseCommand, Margin = new Thickness(8, 0, 0, 0) });
        pause.Children.Add(Row(SymbolRegular.Timer24, Tr.T("Длительность, минут (1–1440)"), row));
        var error = Text("", "ErrorText");
        error.SetBinding(TextBlock.TextProperty, new Binding(nameof(ControlViewModel.PauseError)));
        void ShowError() => error.Visibility = vm.PauseError is null ? Visibility.Collapsed : Visibility.Visible;
        ShowError();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ControlViewModel.PauseError)) ShowError(); };
        pause.Children.Add(error);
        pause.Children.Add(Text(Tr.T("Пауза включается в трее или в меню рабочего стола; пока она идёт, параметры не применяются."), "SubLine"));
        _left.Children.Add(Card(SymbolRegular.PauseCircle24, "PaletteOrangeBrush", Tr.T("Пауза"), pause));

        var lighting = new StackPanel();
        lighting.Children.Add(Bound(SymbolRegular.Camera24, Tr.T("Снимок"), nameof(ControlViewModel.SnapshotText)));
        lighting.Children.Add(Text(Tr.T("Эффекты шести профилей Spectrum, к которым возвращает сброс к заводским."), "SubLine", new Thickness(0, 0, 0, 12)));
        lighting.Children.Add(Buttons(new WpfUiButton { Content = Tr.T("Снять снимок"), Icon = SymbolRegular.Camera24, Command = vm.SnapshotCommand }));
        _right.Children.Add(Card(SymbolRegular.Keyboard24, "PalettePurpleBrush", Tr.T("Подсветка клавиатуры"), lighting));

        var reset = new StackPanel();
        reset.Children.Add(Text(Tr.T("Профили — к заводским: профили и их действия, пресеты «AM · …» и подсветка из снимка. Список программ — очистить: все записи удаляются."),
                                "SubLine", new Thickness(0, 0, 0, 12)));
        reset.Children.Add(Buttons(
            new WpfUiButton { Content = Tr.T("Профили — к заводским"), Icon = SymbolRegular.ArrowReset24, Command = vm.FactoryResetCommand },
            new WpfUiButton { Content = Tr.T("Очистить список программ"), Icon = SymbolRegular.Delete24, Command = vm.ResetProgramsCommand }));
        _right.Children.Add(Card(SymbolRegular.ArrowReset24, "PaletteRedBrush", Tr.T("Сброс"), reset));

        var logs = new StackPanel();
        logs.Children.Add(Row(SymbolRegular.Folder24, vm.LogsPath, new WpfUiButton { Content = Tr.T("Открыть папку"), Icon = SymbolRegular.FolderOpen24, Command = vm.OpenLogsCommand }));
        _right.Children.Add(Card(SymbolRegular.DocumentBulletList24, "PaletteBlueBrush", Tr.T("Логи"), logs));

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
