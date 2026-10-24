using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LegionModes.Core;
using LegionModes.Core.Model;
using LegionModes.Plugin.Ui.ViewModels;
using Wpf.Ui.Common;
using static LegionModes.Plugin.Ui.Look;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using UserControl = System.Windows.Controls.UserControl;

namespace LegionModes.Plugin.Ui;

// Статус: the profile in force and why, the parameters with their source and last result, microphone and camera, who
// holds the laptop awake, the plugin's own health. Cards in two columns, each rebuilt when its view-model data changes.
public partial class StatusTab : UserControl
{
    readonly ContentControl _profile = new(), _devices = new(), _wake = new(), _compute = new(), _screens = new(), _rest = new(), _plugin = new();
    bool _wide;

    // Three columns when the window holds three columns of the width the cards have in two (the owner's screenshot: about 590 screen pixels), else
    // the last card goes under the others. Screen pixels, not device-independent units: the owner judges by his screen.
    const double ColumnMinPixels = 590;
    StatusViewModel? _vm;

    public event Action? EffectiveActionsRequested;

    public StatusTab()
    {
        Resources.MergedDictionaries.Add(SharedResources.Instance);   // before InitializeComponent: StaticResource is resolved while loading
        InitializeComponent();
        _left.Children.Add(_profile);
        _left.Children.Add(_devices);
        _left.Children.Add(_wake);
        _left.Children.Add(_plugin);
        Arrange(false);
        SizeChanged += (_, e) => Arrange(e.NewSize.Width * VisualTreeHelper.GetDpi(this).DpiScaleX >= 3 * ColumnMinPixels + 60);
        IsVisibleChanged += (_, e) => _vm?.Shown((bool)e.NewValue);
    }

    void Arrange(bool wide)
    {
        if (wide == _wide && _right.Children.Count > 0) return;
        _wide = wide;
        _right.Children.Clear();
        _third.Children.Clear();
        _right.Children.Add(_compute);
        _right.Children.Add(_screens);
        (wide ? _third : _right).Children.Add(_rest);
        _gap2.Width = new GridLength(wide ? 5 : 0);
        _col3.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    public void Bind(StatusViewModel vm)
    {
        _vm = vm;
        vm.PropertyChanged += OnChanged;
        if (IsVisible) vm.Shown(true);
        foreach (var part in Enum.GetValues<Part>()) Render(part);
    }

    enum Part { Errors, Profile, Devices, Wake, Parameters, Plugin }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        Part? part = e.PropertyName switch
        {
            nameof(StatusViewModel.Errors) => Part.Errors,
            nameof(StatusViewModel.InForceName) or nameof(StatusViewModel.InForceReason) or nameof(StatusViewModel.InForceTone)
                or nameof(StatusViewModel.ProfileRows) => Part.Profile,
            nameof(StatusViewModel.DeviceRows) => Part.Devices,
            nameof(StatusViewModel.Wake) or nameof(StatusViewModel.WakeState) => Part.Wake,
            nameof(StatusViewModel.Parameters) => Part.Parameters,
            nameof(StatusViewModel.PluginRows) => Part.Plugin,
            _ => null,
        };
        if (part is { } p)
        {
            try { Render(p); }
            catch (Exception ex) { Log.Error("status render " + p, ex); }
        }
    }

    void Render(Part part)
    {
        if (_vm is not { } vm) return;
        switch (part)
        {
            case Part.Errors:
                _errors.Children.Clear();
                foreach (var e in vm.Errors) _errors.Children.Add(Banner(e));
                break;
            case Part.Profile:
                _profile.Content = ProfileCard(vm);
                break;
            case Part.Devices:
                _devices.Content = Card(SymbolRegular.Mic24, "PaletteGreenBrush", Tr.T("Microphone and camera"), Rows(vm.DeviceRows));
                break;
            case Part.Wake:
                _wake.Content = WakeCard(vm);
                break;
            case Part.Parameters:
                (_compute.Content, _screens.Content, _rest.Content) = ParameterCards(vm);
                break;
            case Part.Plugin:
                _plugin.Content = Card(SymbolRegular.ShieldCheckmark24, "PaletteLightBlueBrush", Tr.T("Plugin"), Rows(vm.PluginRows));
                break;
        }
    }

    UIElement ProfileCard(StatusViewModel vm)
    {
        var body = new StackPanel();
        body.Children.Add(Text(vm.InForceName, "MainValue"));
        body.Children.Add(Toned(vm.InForceReason, "SubLine", vm.InForceTone, new Thickness(0, 2, 0, 12)));
        body.Children.Add(Rows(vm.ProfileRows));
        var button = new Wpf.Ui.Controls.Button { Content = Tr.T("Effective actions"), Icon = SymbolRegular.Flowchart24, IsEnabled = vm.Decision is not null };
        button.Click += (_, _) =>
        {
            try { EffectiveActionsRequested?.Invoke(); }
            catch (Exception e) { Log.Error("effective actions", e); }
        };
        return Card(SymbolRegular.Layer24, "PaletteLightBlueBrush", Tr.T("In force now|2"), body, button);
    }

    // The parameters in three cards (owner 2026-10-24, the numbers of his screenshot): the compute pair (GPU mode, processor), the screens
    // (panel, external monitor), and the rest.
    static readonly ParameterId[] Compute = [ParameterId.Gpu, ParameterId.Processor];
    static readonly ParameterId[] Screens = [ParameterId.Panel, ParameterId.External];

    static (UIElement? Compute, UIElement? Screens, UIElement Others) ParameterCards(StatusViewModel vm)
    {
        var compute = new StackPanel();
        var screens = new StackPanel();
        var rest = new StackPanel();
        if (vm.Parameters.Count == 0) rest.Children.Add(Text(Tr.T("no profile selected yet"), "SubLine"));
        foreach (var p in vm.Parameters)
            (Compute.Contains(p.Id) ? compute : Screens.Contains(p.Id) ? screens : rest).Children.Add(ParameterItem(p));
        return (compute.Children.Count > 0 ? Card(SymbolRegular.DeveloperBoard24, "PaletteLightBlueBrush", Tr.T("Processor and graphics"), compute) : null,
                screens.Children.Count > 0 ? Card(SymbolRegular.Desktop24, "PalettePurpleBrush", Tr.T("Screens"), screens) : null,
                Card(SymbolRegular.Options24, "PaletteBlueBrush", Tr.T("Parameters"), rest));
    }

    // Name and, when it is not a plain success, the result on one line; the value under it, then where it comes from (and the result's text).
    static UIElement ParameterItem(ParameterLine p)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var icon = new SymbolIcon { Symbol = Texts.ParameterIcon[p.Id] };
        icon.SetResourceReference(StyleProperty, "RowIcon");
        Place(grid, icon, 0, 0);
        Place(grid, Text(p.Name, "ItemName"), 0, 1);
        if (p.Result.Length > 0) Place(grid, Toned(p.Result, "RowValue", p.Tone, new Thickness(12, 0, 0, 0)), 0, 2);
        var lines = new StackPanel();
        lines.Children.Add(Text(p.Value, "SubLine", new Thickness(0, 2, 0, 0)));
        lines.Children.Add(Text(p.Source, "SubLine"));
        if (p.Facts is { } facts)
            foreach (var f in facts) lines.Children.Add(Toned(f.Label + ": " + f.Value, "SubLine", f.Tone, null));
        if (p.Detail is { } d) lines.Children.Add(Toned(d, "SubLine", p.Tone, null));
        Place(grid, lines, 1, 1);
        Grid.SetColumnSpan(lines, 2);
        return grid;
    }

    static UIElement WakeCard(StatusViewModel vm)
    {
        var body = new StackPanel();
        if (vm.WakeState.Length > 0) body.Children.Add(Text(vm.WakeState, "SubLine"));
        foreach (var w in vm.Wake)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            item.Children.Add(Text(w.Name, "ItemName"));
            item.Children.Add(Text(w.What, "SubLine", new Thickness(0, 2, 0, 0)));
            body.Children.Add(item);
        }
        return Card(SymbolRegular.WeatherMoon24, "PalettePurpleBrush", Tr.T("What keeps it awake"), body);
    }

    static void Place(Grid grid, UIElement e, int row, int column)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, column);
        grid.Children.Add(e);
    }

    static UIElement Rows(IReadOnlyList<StatusLine> rows)
    {
        var panel = new StackPanel();
        foreach (var r in rows) panel.Children.Add(Line(r));
        return panel;
    }

    // "Label: value" with the label's icon; the value wraps under itself.
    internal static UIElement Line(StatusLine r)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new SymbolIcon { Symbol = LineIcon(r.Label) };
        icon.SetResourceReference(StyleProperty, "RowIcon");
        Place(grid, icon, 0, 0);
        var label = Text(r.Label + ":", "RowLabel", new Thickness(0, 0, 4, 0));
        label.VerticalAlignment = VerticalAlignment.Top;
        Place(grid, label, 0, 1);
        var value = Toned(r.Value, "RowValue", r.Tone, null);
        value.VerticalAlignment = VerticalAlignment.Top;
        Place(grid, value, 0, 2);
        icon.VerticalAlignment = VerticalAlignment.Top;
        return grid;
    }

    // The label arrives already translated from the view-model, so each Russian key is matched in both forms.
    static SymbolRegular LineIcon(string label)
    {
        (string Ru, SymbolRegular Icon)[] icons =
        {
            ("Conditions", SymbolRegular.Filter24),
            ("Control", SymbolRegular.Settings24),
            ("Waiting", SymbolRegular.Clock24),
            ("On", SymbolRegular.Flash24),
            ("Rule", SymbolRegular.Info24),
            ("Sleep with lid closed", SymbolRegular.WeatherMoon24),
            ("Last apply", SymbolRegular.ArrowSync24),
            ("Message", SymbolRegular.Warning24),
            ("Self-test", SymbolRegular.ShieldCheckmark24),
            ("LLT actions", SymbolRegular.Flowchart24),
            ("Hardware", SymbolRegular.DeveloperBoard24),
            ("Tray and home", SymbolRegular.Window24),
            ("LLT texts", SymbolRegular.Translate24),
            ("Microphone", SymbolRegular.Mic24),
            ("Camera", SymbolRegular.Camera24),
            ("Switch", SymbolRegular.ToggleLeft24),
            ("Used by", SymbolRegular.Apps24),
            ("Latest release", SymbolRegular.ArrowDownload24),
            ("Last check", SymbolRegular.Clock24),
            ("Compatibility", SymbolRegular.ShieldCheckmark24),
        };
        foreach (var (ru, icon) in icons)
            if (label == ru || label == Tr.T(ru)) return icon;
        return SymbolRegular.CircleSmall24;
    }

    internal static TextBlock Toned(string text, string style, Tone tone, Thickness? margin)
    {
        var t = Text(text, style, margin);
        var brush = tone switch
        {
            Tone.Good => "PaletteGreenBrush",
            Tone.Bad => "SystemFillColorCriticalBrush",
            Tone.Warn => "PaletteOrangeBrush",
            Tone.Muted => "TextFillColorTertiaryBrush",
            _ => null,
        };
        if (brush is not null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    static UIElement Banner(string text)
    {
        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(12),
            Child = Toned(text, "RowValue", Tone.Bad, null),
        };
        border.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCriticalBackgroundBrush");
        return border;
    }
}
