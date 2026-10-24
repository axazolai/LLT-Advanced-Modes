using System.Windows;
using System.Windows.Controls;
using LegionModes.Core;
using LegionModes.Plugin.Ui.ViewModels;
using Wpf.Ui.Common;
using static LegionModes.Plugin.Ui.Look;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using WpfUiButton = Wpf.Ui.Controls.Button;

namespace LegionModes.Plugin.Ui;

// Обновления: LLT's version and the last check with the update buttons, the history, the changelog. Redrawn after every
// read of updates.json; the buttons and the message line stay bound to the view model.
public partial class UpdatesTab : UserControl
{
    UpdatesViewModel? _vm;
    readonly ContentControl _version = new(), _history = new(), _changelog = new();
    UIElement? _buttons;

    public UpdatesTab()
    {
        Resources.MergedDictionaries.Add(SharedResources.Instance);   // before InitializeComponent: StaticResource is resolved while loading
        InitializeComponent();
        _left.Children.Add(_version);
        _left.Children.Add(_history);
        _right.Children.Add(_changelog);
    }

    public void Bind(UpdatesViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        _buttons = Buttons(vm);
        vm.Changed += () =>
        {
            try { Render(); }
            catch (Exception e) { Log.Error("updates render", e); }
        };
        Render();
    }

    static UIElement Buttons(UpdatesViewModel vm)
    {
        var panel = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new WpfUiButton { Content = Tr.T("Check now"), Icon = SymbolRegular.ArrowSync24, Command = vm.CheckCommand });
        panel.Children.Add(row);
        var message = new TextBlock();
        message.SetResourceReference(StyleProperty, "MessageText");
        message.SetBinding(TextBlock.TextProperty, new Binding(nameof(TabViewModel.Message)));
        message.SetBinding(VisibilityProperty, new Binding(nameof(TabViewModel.HasMessage)) { Converter = new BooleanToVisibilityConverter() });
        panel.Children.Add(message);
        return panel;
    }

    void Render()
    {
        if (_vm is not { } vm) return;
        if (_buttons is not null && LogicalTreeHelper.GetParent(_buttons) is System.Windows.Controls.Panel old) old.Children.Remove(_buttons);

        var version = new StackPanel();
        version.Children.Add(Text(vm.Installed, "MainValue"));
        version.Children.Add(Text(Tr.T("installed"), "SubLine", new Thickness(0, 2, 0, 12)));
        version.Children.Add(StatusTab.Line(new StatusLine(Tr.T("Latest release"), vm.Latest)));
        version.Children.Add(StatusTab.Line(new StatusLine(Tr.T("Last check"), vm.LastCheck)));
        if (vm.Compatibility.Length > 0) version.Children.Add(StatusTab.Line(new StatusLine(Tr.T("Compatibility"), vm.Compatibility, vm.CompatibilityTone)));
        version.Children.Add(StatusTab.Toned(Tr.F("LLT is pinned to {0}: a newer version is installed only from the project after its analysis", vm.Installed), "SubLine", Tone.Muted, new Thickness(0, 4, 0, 8)));
        if (vm.HasProblems) version.Children.Add(StatusTab.Toned(vm.Problems, "SubLine", Tone.Bad, new Thickness(0, 0, 0, 10)));
        if (_buttons is not null) version.Children.Add(_buttons);
        var reread = new WpfUiButton { Icon = SymbolRegular.ArrowClockwise24, ToolTip = Tr.T("Reread"), Command = vm.RefreshCommand };
        _version.Content = Card(SymbolRegular.ArrowDownload24, "PaletteLightBlueBrush", "Lenovo Legion Toolkit", version, reread);

        var history = new StackPanel();
        if (vm.History.Count == 0) history.Children.Add(Text(Tr.T("no updates yet"), "SubLine"));
        foreach (var h in vm.History) history.Children.Add(HistoryItem(h));
        _history.Content = Card(SymbolRegular.History24, "PalettePurpleBrush", Tr.T("Update history"), history);

        var changelog = new StackPanel();
        if (vm.Changelog.Count == 0) changelog.Children.Add(Text(Tr.T("no data: the changelog appears after a check"), "SubLine"));
        foreach (var line in vm.Changelog)
        {
            // The release notes are markdown: a heading as an item name, a list item with a bullet, blank lines dropped.
            var raw = line.Line.Trim();
            if (raw.Length == 0) continue;
            if (raw.StartsWith('#'))
            {
                changelog.Children.Add(Text(raw.TrimStart('#', ' '), "ItemName", new Thickness(0, 6, 0, 6)));
                continue;
            }
            if (raw.StartsWith("- ", StringComparison.Ordinal) || raw.StartsWith("* ", StringComparison.Ordinal)) raw = "•  " + raw[2..];
            var t = StatusTab.Toned(raw, "RowValue", line.Highlight ? Tone.Warn : Tone.Normal, new Thickness(0, 0, 0, 6));
            if (line.Highlight) t.FontWeight = FontWeights.SemiBold;
            changelog.Children.Add(t);
        }
        if (vm.Changelog.Any(l => l.Highlight))
            changelog.Children.Insert(0, Text(Tr.T("Highlighted: what concerns the LLT parts the plugin uses."), "SubLine", new Thickness(0, 0, 0, 10)));
        _changelog.Content = Card(SymbolRegular.DocumentBulletList24, "PaletteGreenBrush", Tr.T("What's new"), changelog);
    }

    // Version and result on one line, the time under it, then what went wrong.
    static UIElement HistoryItem(HistoryLine h)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(Text(h.Version, "ItemName"));
        left.Children.Add(Text(h.When, "SubLine", new Thickness(0, 2, 0, 0)));
        if (h.Detail is { } d) left.Children.Add(StatusTab.Toned(d, "SubLine", h.Tone, null));
        grid.Children.Add(left);
        var result = StatusTab.Toned(h.Result, "RowValue", h.Tone, new Thickness(12, 0, 0, 0));
        result.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(result, 1);
        grid.Children.Add(result);
        return grid;
    }
}
