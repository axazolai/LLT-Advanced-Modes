using System.Windows;
using System.Windows.Controls;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using LegionModes.Core;
using LegionModes.Core.Model;
using LegionModes.Plugin.Ui.ViewModels;
using LenovoLegionToolkit.WPF.Controls;
using LenovoLegionToolkit.WPF.Windows;
using Wpf.Ui.Common;
using CardControl = LenovoLegionToolkit.WPF.Controls.Custom.CardControl;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using WpfUiButton = Wpf.Ui.Controls.Button;
using WpfUiTextBox = Wpf.Ui.Controls.TextBox;

namespace LegionModes.Plugin.Ui;

// The conditions of a profile, laid out as LLT's "Create Action" window (CreateAutomationPipelineWindow.xaml): a filter
// box, one card per axis value with its icon, a chevron on a free value and a checkmark on the chosen one. Values of one
// axis exclude each other; clicking the chosen value again clears the axis ("any"). A value that would give the
// combination of another automatic profile names that profile; the confirm button stays disabled while the chosen
// combination belongs to another profile (a partial choice may lead on to a free one, so the cards stay clickable).
public sealed class ConditionWindow : BaseWindow
{
    sealed record Value(string Axis, string Label, SymbolRegular Icon, Func<Conditions, Conditions> Set, Func<Conditions, bool> IsOn);

    // The axis names are Russian identifiers (Clear switches on them); AxisHeader and the filter show Tr.T(axis).
    static (string Axis, SymbolRegular Icon, Value[] Values)[] Axes => ConditionAxes();

    static (string Axis, SymbolRegular Icon, Value[] Values)[] ConditionAxes() =>
    [
        ("Power source", SymbolRegular.PlugConnected24,
        [
            new("Power source", Tr.T("Mains"), SymbolRegular.PlugConnected24, c => c with { Source = PowerSource.Ac }, c => c.Source == PowerSource.Ac),
            new("Power source", Tr.T("Weak adapter"), SymbolRegular.UsbPlug24, c => c with { Source = PowerSource.Weak }, c => c.Source == PowerSource.Weak),
            new("Power source", Tr.T("Battery"), SymbolRegular.Battery524, c => c with { Source = PowerSource.Battery }, c => c.Source == PowerSource.Battery),
        ]),
        ("Laptop", SymbolRegular.Laptop24,
        [
            new("Laptop", Tr.T("Open"), SymbolRegular.Laptop24, c => c with { Lid = LidState.Open }, c => c.Lid == LidState.Open),
            new("Laptop", Tr.T("Lid closed"), SymbolRegular.Sleep24, c => c with { Lid = LidState.Closed }, c => c.Lid == LidState.Closed),
        ]),
        ("Workload", SymbolRegular.Gauge24,
        [
            new("Workload", Tr.T("Heavy"), SymbolRegular.Flash24, c => c with { Load = Workload.Heavy }, c => c.Load == Workload.Heavy),
            new("Workload", Tr.T("AI"), SymbolRegular.BrainCircuit24, c => c with { Load = Workload.Ai }, c => c.Load == Workload.Ai),
            new("Workload", Tr.T("Work"), SymbolRegular.Window24, c => c with { Load = Workload.Work }, c => c.Load == Workload.Work),
            new("Workload", Tr.T("Idle"), SymbolRegular.WeatherMoon24, c => c with { Load = Workload.Idle }, c => c.Load == Workload.Idle),
        ]),
        ("External monitor", SymbolRegular.Desktop24,
        [
            new("External monitor", Tr.T("Connected"), SymbolRegular.Desktop24, c => c with { Monitor = MonitorState.Connected }, c => c.Monitor == MonitorState.Connected),
            new("External monitor", Tr.T("Not connected"), SymbolRegular.Laptop24, c => c with { Monitor = MonitorState.None }, c => c.Monitor == MonitorState.None),
        ]),
    ];

    readonly ProfilesViewModel _vm;
    readonly Guid? _except;
    readonly StackPanel _content = new() { Margin = new Thickness(0, 0, 12, 0) };
    readonly WpfUiTextBox _search = new() { Margin = new Thickness(12, 0, 12, 12), Icon = SymbolRegular.Search24, PlaceholderText = Tr.T("Filter") };
    readonly TextBlock _summary = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly WpfUiButton _ok = new() { MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), Appearance = ControlAppearance.Primary };
    Conditions _current;
    Conditions? _result;

    ConditionWindow(ProfilesViewModel vm, Conditions start, Guid? except, string okText)
    {
        _vm = vm;
        _except = except;
        _current = start;
        Title = Tr.T("Profile conditions");
        Width = 600;
        Height = 640;
        MinWidth = 500;
        MinHeight = 400;
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _ok.Content = okText;
        _ok.Click += (_, _) => { _result = _current; Close(); };
        _search.TextChanged += (_, _) => SafeRender();
        Content = Layout();
        Render();
    }

    // Returns the chosen conditions, or null when cancelled.
    public static Task<Conditions?> AskAsync(Window? owner, ProfilesViewModel vm, Conditions? start, Guid? except, string? okText = null)
    {
        var w = new ConditionWindow(vm, start ?? new Conditions(), except, okText ?? Tr.T("Create")) { Owner = owner };
        w.ShowDialog();
        return Task.FromResult(w._result);
    }

    UIElement Layout()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new Wpf.Ui.Controls.TitleBar { CanMaximize = false, ShowMaximize = false, ShowMinimize = false, UseSnapLayout = false });
        var title = new TextBlock { Text = Title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        grid.Children.Add(new Grid { Height = 24, Margin = new Thickness(12, 8, 12, 8), Children = { title } });

        Grid.SetRow(_search, 1);
        grid.Children.Add(_search);

        var scroll = new Wpf.Ui.Controls.DynamicScrollViewer { Margin = new Thickness(12, 0, 0, 12), Content = _content };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 0, 12, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cancel = new WpfUiButton { MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), Content = Tr.T("Cancel") };
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(_ok, 1);
        Grid.SetColumn(cancel, 2);
        footer.Children.Add(_summary);
        footer.Children.Add(_ok);
        footer.Children.Add(cancel);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);
        return grid;
    }

    void Render()
    {
        _content.Children.Clear();
        var filter = _search.Text?.Trim() ?? "";
        foreach (var (axis, icon, values) in Axes)
        {
            var shown = values.Where(v => filter.Length == 0 || v.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                          || Tr.T(axis).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (shown.Count == 0) continue;
            _content.Children.Add(AxisHeader(axis, icon));
            foreach (var v in shown) _content.Children.Add(ValueCard(v));
        }

        var holder = _vm.Holder(_current, _except);
        var empty = _current.Specificity == 0;
        _ok.IsEnabled = !empty && holder is null;
        _summary.Text = empty ? Tr.T("Choose at least one condition")
            : holder is not null ? Tr.F("Profile \"{0}\" already has these conditions", holder.Name)
            : Tr.F("Profile: {0}", ProfileNames.FromConditionsShown(_current));
    }

    static UIElement AxisHeader(string axis, SymbolRegular icon)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new SymbolIcon { Symbol = icon, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = Tr.T(axis), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(text);
        ((SymbolIcon)panel.Children[0]).SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return panel;
    }

    UIElement ValueCard(Value v)
    {
        var on = v.IsOn(_current);
        var next = on ? Clear(v.Axis, _current) : v.Set(_current);
        var holder = on ? null : _vm.Holder(next, _except);

        var mark = new SymbolIcon { Symbol = on ? SymbolRegular.Checkmark24 : SymbolRegular.ChevronRight24 };
        if (on) mark.SetResourceReference(Control.ForegroundProperty, "SystemAccentColorSecondaryBrush");
        var header = new CardHeaderControl { Title = v.Label, Accessory = mark };
        if (holder is not null) header.Warning = Tr.F("with it: same as profile \"{0}\"", holder.Name);

        var card = new CardControl { Icon = v.Icon, Header = header, Margin = new Thickness(0, 8, 0, 0) };
        card.Click += (_, _) => { _current = next; SafeRender(); };
        return card;
    }

    static Conditions Clear(string axis, Conditions c) => axis switch
    {
        "Power source" => c with { Source = null },
        "Laptop" => c with { Lid = null },
        "Workload" => c with { Load = null },
        _ => c with { Monitor = null },
    };

    // Render runs from WPF event handlers: nothing may escape into LLT's dispatcher.
    void SafeRender()
    {
        try { Render(); }
        catch (Exception e) { Log.Error("condition window render", e); }
    }
}
