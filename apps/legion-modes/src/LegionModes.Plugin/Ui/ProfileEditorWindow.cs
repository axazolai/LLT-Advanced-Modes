using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Core.Ui;
using LegionModes.Plugin.Ui.ViewModels;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows;
using Wpf.Ui.Common;
using static LegionModes.Plugin.Ui.Look;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using WpfUiButton = Wpf.Ui.Controls.Button;

namespace LegionModes.Plugin.Ui;

// The editor of one profile, laid out as LLT's DeviceInformationWindow (spec section 5, "Visual reference"): blocks in
// three columns — the profile, overlaps, actions and the nine parameters — placed by the editor layout (one for every
// profile, owner). «Расположение» in the title row lets the blocks be moved as on a kanban board.
public sealed class ProfileEditorWindow : BaseWindow
{
    readonly ProfileEditorViewModel _vm;
    readonly StackPanel[] _columns = [new() { Margin = new Thickness(0, 0, 5, 0) }, new() { Margin = new Thickness(5, 0, 5, 0) }, new() { Margin = new Thickness(5, 0, 0, 0) }];
    readonly Grid _board = new() { Margin = new Thickness(16, 0, 16, 12) };
    readonly Wpf.Ui.Controls.DynamicScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock _title = new();
    readonly TextBlock _errors = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly WpfUiButton _save = new() { Content = Tr.T("Save"), Appearance = ControlAppearance.Primary, MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
    readonly WpfUiButton _layoutButton = new() { Icon = SymbolRegular.Board24, Content = Tr.T("Layout"), Appearance = ControlAppearance.Transparent, Margin = new Thickness(16, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), ToolTip = Tr.T("Edit the block layout") };
    readonly Dictionary<string, Block> _blocks = [];
    readonly Dictionary<ParameterId, ParameterCard> _cards = [];
    readonly BoardDrag _drag;
    bool _layoutMode;
    bool _closingConfirmed;

    // One block of the board: its card and the handle shown while the layout is edited.
    sealed record Block(string Key, Border Root, ContentControl Body, FrameworkElement Handle);

    ProfileEditorWindow(ProfilesViewModel list, Profile p)
    {
        _vm = new ProfileEditorViewModel(list, p);
        Title = ProfileNames.Display(p);
        Width = 1240;
        Height = 860;
        MinWidth = 960;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(SharedResources.Instance);
        _errors.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");

        for (var i = 0; i < 3; i++)
        {
            _board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_columns[i], i);
            _board.Children.Add(_columns[i]);
        }
        _scroll.Content = _board;
        _drag = new BoardDrag(_columns, _scroll, _board, SaveLayout);

        AddBlock(EditorLayout.Profile, SymbolRegular.Layer24, "PaletteLightBlueBrush", Tr.T("Profile"));
        AddBlock(EditorLayout.Overlaps, SymbolRegular.BranchFork24, "PaletteOrangeBrush", Tr.T("Overlaps"));
        AddBlock(EditorLayout.Actions, SymbolRegular.Flowchart24, "PalettePurpleBrush", Tr.T("LLT actions"));
        foreach (var id in Enum.GetValues<ParameterId>())
        {
            var block = AddBlock(id.ToString(), Texts.ParameterIcon[id], ParameterBrush(id), Texts.Parameter[id]);
            _cards[id] = new ParameterCard(this, id, block.Body);
        }
        Place(EditorLayout.Load(_vm.Host.EditorLayoutPath));

        _layoutButton.Click += (_, _) => SetLayoutMode(!_layoutMode);
        Content = Frame(SymbolRegular.Layer24, "PaletteLightBlueBrush", _title, _scroll, Footer(), _layoutButton);
        _vm.Changed += Refresh;
        Closing += OnClosing;
        Refresh();
    }

    public static void Open(Window? owner, ProfilesViewModel vm, Profile p) =>
        new ProfileEditorWindow(vm, p) { Owner = owner }.ShowDialog();

    static string ParameterBrush(ParameterId id) =>
        new[] { "PaletteLightBlueBrush", "PaletteGreenBrush", "PalettePurpleBrush", "PaletteBlueBrush" }[((int)id - 1) % 4];

    Block AddBlock(string key, SymbolRegular icon, string brush, string title)
    {
        var body = new ContentControl();
        var handle = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.SizeAll, Visibility = Visibility.Collapsed,
            Padding = new Thickness(6, 2, 0, 2), ToolTip = Tr.T("Drag the block"),
        };
        var dots = new SymbolIcon { Symbol = SymbolRegular.ReOrderDotsVertical24, FontSize = 16 };
        dots.SetResourceReference(Control.ForegroundProperty, "TextFillColorTertiaryBrush");
        handle.Child = dots;
        var root = Card(icon, brush, title, body, handle);
        var block = new Block(key, root, body, handle);
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            try { _drag.Begin(root, handle, e); }
            catch (Exception ex) { Log.Error("block drag", ex); }
        };
        _blocks[key] = block;
        return block;
    }

    void Place(EditorLayout layout)
    {
        foreach (var c in _columns) c.Children.Clear();
        for (var i = 0; i < 3; i++)
            foreach (var key in layout.Columns[i])
                if (_blocks.TryGetValue(key, out var b)) _columns[i].Children.Add(b.Root);
    }

    void SetLayoutMode(bool on)
    {
        _layoutMode = on;
        foreach (var b in _blocks.Values) b.Handle.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _layoutButton.Content = on ? Tr.T("Done") : Tr.T("Layout");
        _layoutButton.Appearance = on ? ControlAppearance.Primary : ControlAppearance.Transparent;
        Refresh();                                       // an empty Пересечения block shows while the layout is edited
    }

    // After every drop: the order of the blocks in the columns becomes the layout for every profile.
    void SaveLayout()
    {
        try
        {
            var columns = _columns.Select(c => (IReadOnlyList<string>)c.Children.OfType<Border>()
                .Select(r => _blocks.Values.FirstOrDefault(b => b.Root == r)?.Key).OfType<string>().ToList()).ToList();
            EditorLayout.Save(_vm.Host.EditorLayoutPath, EditorLayout.Normalize(new EditorLayout(columns)));
        }
        catch (Exception e)
        {
            Log.Error("editor layout save", e);
            _errors.Text = Tr.F("Layout not saved: {0}", e.Message);
        }
    }

    UIElement Footer()
    {
        var footer = new Grid { Margin = new Thickness(16, 0, 16, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cancel = new WpfUiButton { Content = Tr.T("Cancel"), MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        _save.Click += async (_, _) => await SaveAsync();
        Grid.SetColumn(_save, 1);
        Grid.SetColumn(cancel, 2);
        footer.Children.Add(_errors);
        footer.Children.Add(_save);
        footer.Children.Add(cancel);
        return footer;
    }

    // Every change of the draft comes here from a WPF event handler: nothing may escape into LLT's dispatcher.
    void Refresh()
    {
        try { RefreshCore(); }
        catch (Exception e)
        {
            Log.Error("profile editor refresh", e);
            _errors.Text = Tr.F("Error: {0}", e.Message);
            _save.IsEnabled = false;
        }
    }

    // The «Профиль» block is rebuilt only when what it shows changes shape (the conditions); typing in its fields updates its
    // title in place, so the field under the cursor stays.
    Conditions? _profileShownFor;
    TextBlock? _profileTitle;

    void RefreshCore()
    {
        _title.Text = Tr.F("Profile {0} · {1}", _vm.Index, _vm.Name) + (_vm.Dirty ? Tr.T(" — modified") : "");
        if (_profileTitle is null || _profileShownFor != _vm.Conditions)
        {
            _profileShownFor = _vm.Conditions;
            _blocks[EditorLayout.Profile].Body.Content = ProfileBody();
        }
        else _profileTitle.Text = $"{_vm.Index} · {_vm.Name}";
        var overlaps = _vm.Overlaps;
        _blocks[EditorLayout.Overlaps].Body.Content = OverlapsBody(overlaps);
        _blocks[EditorLayout.Overlaps].Root.Visibility = overlaps.Count > 0 || _layoutMode ? Visibility.Visible : Visibility.Collapsed;
        _blocks[EditorLayout.Actions].Body.Content = ActionsBody();
        foreach (var card in _cards.Values) card.Refresh(rebuild: false);
        var errors = _vm.Errors;
        _errors.Text = errors.Count == 0 ? "" : string.Join("; ", errors);
        _save.IsEnabled = _vm.Dirty && errors.Count == 0;
    }

    async Task SaveAsync()
    {
        try
        {
            var error = await _vm.SaveAsync();
            if (error is not null) { _errors.Text = error; return; }
            _closingConfirmed = true;
            Close();
        }
        catch (Exception e)
        {
            Log.Error("profile save", e);
            _errors.Text = Tr.F("Not saved: {0}", e.Message);
        }
    }

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed || !_vm.Dirty) return;
        e.Cancel = true;
        try
        {
            if (!await MessageBoxHelper.ShowAsync(this, Tr.T("Close without saving?"), Tr.T("The profile changes will be lost."), Tr.T("Close"), Tr.T("Stay"))) return;
            _closingConfirmed = true;
            Close();
        }
        catch (Exception ex) { Log.Error("profile editor closing", ex); }
    }

    // ---- block bodies ----

    UIElement ProfileBody()
    {
        var body = new StackPanel();
        _profileTitle = Text($"{_vm.Index} · {_vm.Name}", "MainValue", new Thickness(0, 0, 0, 12));
        body.Children.Add(_profileTitle);
        if (_vm.IsAutomatic)
        {
            var name = new Wpf.Ui.Controls.TextBox { Text = _vm.Name, MinWidth = 220 };
            name.TextChanged += (_, _) => { var n = name.Text.Trim(); if (n.Length > 0 && n != _vm.Name) _vm.SetName(n); };
            body.Children.Add(Row(SymbolRegular.Rename24, Tr.T("Name"), name));

            var change = new WpfUiButton { Content = Tr.T("Change…"), Margin = new Thickness(8, 0, 0, 0) };
            change.Click += async (_, _) =>
            {
                try
                {
                    var c = await ConditionWindow.AskAsync(this, _vm.List, _vm.Conditions, _vm.Original.Id, Tr.T("Select"));
                    if (c is not null && c != _vm.Conditions) _vm.SetConditions(c);
                }
                catch (Exception e) { Log.Error("profile conditions", e); _errors.Text = Tr.F("Error: {0}", e.Message); }
            };
            var conditions = new StackPanel { Orientation = Orientation.Horizontal };
            conditions.Children.Add(Text(ProfileNames.FromConditionsShown(_vm.Conditions), "RowValue"));
            conditions.Children.Add(change);
            body.Children.Add(Row(SymbolRegular.Filter24, Tr.T("Conditions"), conditions));
            body.Children.Add(Row(SymbolRegular.Timer24, Tr.T("Switch-on delay, s"), Number(_vm.DelaySec, 0, 3600, _vm.SetDelay)));
            body.Children.Add(Row(SymbolRegular.Hourglass24, Tr.T("Hold after, s"), Number(_vm.HoldSec, 0, 3600, _vm.SetHold)));
        }
        else
        {
            body.Children.Add(Text(_vm.IsRoot
                ? Tr.T("In force when no other profile matches, and gives values to every profile that sets none. It has no conditions and all nine parameters are set.")
                : Tr.T("Switched on by hand (tray, desktop menu), on battery only; inherits only from \"Always\"."), "SubLine"));
        }
        return body;
    }

    UIElement OverlapsBody(IReadOnlyList<Overlap> overlaps)
    {
        var body = new StackPanel();
        if (overlaps.Count == 0) body.Children.Add(Text(Tr.T("No overlaps."), "SubLine"));
        foreach (var o in overlaps)
        {
            var both = new Conditions(_vm.Conditions.Source ?? o.Other.Conditions.Source, _vm.Conditions.Lid ?? o.Other.Conditions.Lid,
                _vm.Conditions.Load ?? o.Other.Conditions.Load, _vm.Conditions.Monitor ?? o.Other.Conditions.Monitor);
            body.Children.Add(Text(Tr.F("matches \"{0}\" when \"{1}\", \"{2}\" wins", ProfileNames.Display(o.Other), ProfileNames.FromConditionsShown(both), ProfileNames.Display(o.Winner)),
                "RowValue", new Thickness(0, 0, 0, 6)));
        }
        if (overlaps.Count > 0)
            body.Children.Add(Text(Tr.T("When conditions are equally specific, the profile higher in the list is in force."), "SubLine", new Thickness(0, 4, 0, 0)));
        return body;
    }

    UIElement ActionsBody()
    {
        var body = new StackPanel();
        var pipelines = _vm.Pipelines;
        if (pipelines.Count == 0) body.Children.Add(Text(Tr.T("No actions."), "SubLine"));
        foreach (var e in pipelines)
        {
            var state = e.State == PipelineState.Wins ? "" : " · " + EffectiveActionsWindow.NotRunningText(e);
            var from = e.Source.Id == _vm.Original.Id ? "" : Tr.F(" · ↳ from \"{0}\"", ProfileNames.Display(e.Source));
            body.Children.Add(Text(PipelineName(e.Pipeline), "ItemName"));
            body.Children.Add(Text(KeyText(e.Pipeline.Key) + from + state, "SubLine", new Thickness(0, 0, 0, 8)));
        }
        return body;
    }

    internal static string KeyText(string key) => key switch
    {
        PipelineKeys.Enter => Tr.T("on entering the profile"),
        PipelineKeys.Exit => Tr.T("on leaving the profile"),
        _ => Tr.T("on an LLT event"),
    };

    internal static string PipelineName(Pipeline p)
    {
        try
        {
            using var doc = JsonDocument.Parse(p.Llt);
            if (doc.RootElement.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } s) return Tr.T(PipelineNames.Canonical(s));
        }
        catch (JsonException) { }
        try
        {
            // As LLT's own header: no name -> the event's name.
            if (Pipelines.PipelineJson.Read(p.Llt).Trigger is { } t) return t.DisplayName;
        }
        catch (Exception e) { Log.Trace("pipeline name: " + e.Message); }
        return Tr.T("Unnamed");
    }

    static UIElement Number(int value, int min, int max, Action<int> changed)
    {
        var box = new Wpf.Ui.Controls.NumberBox { Value = value, Minimum = min, Maximum = max, MaxDecimalPlaces = 0, Width = 120 };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) && v >= min && v <= max && v != value)
            {
                value = v;
                changed(v);
            }
        };
        return box;
    }

    // One parameter: the value in force (15 Bold), where it comes from, its controls when the profile sets it, and
    // «Задать здесь» / «Вернуть унаследованное».
    sealed class ParameterCard
    {
        readonly ProfileEditorWindow _w;
        readonly ParameterId _id;
        readonly TextBlock _value = Text("", "MainValue");
        readonly TextBlock _source = Text("", "SubLine", new Thickness(0, 2, 0, 12));
        readonly ContentControl _controls = new();
        readonly WpfUiButton _toggle = new() { Appearance = ControlAppearance.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        bool? _shownSet;

        public ParameterCard(ProfileEditorWindow w, ParameterId id, ContentControl host)
        {
            _w = w;
            _id = id;
            _toggle.Click += (_, _) =>
            {
                try
                {
                    if (_w._vm.Set.IsSet(_id)) _w._vm.Inherit(_id);
                    else _w._vm.Own(_id);
                    Refresh(rebuild: true);
                }
                catch (Exception e) { Log.Error("parameter inherit / own", e); _w._errors.Text = Tr.F("Error: {0}", e.Message); }
            };
            var body = new StackPanel();
            body.Children.Add(_value);
            body.Children.Add(_source);
            body.Children.Add(_controls);
            body.Children.Add(_toggle);
            host.Content = body;
        }

        public void Refresh(bool rebuild)
        {
            var vm = _w._vm;
            var (value, source) = vm.EffectiveOf(_id);
            var own = vm.Set.IsSet(_id);
            _value.Text = ValueText.Of(_id, value, ProfileNames.Display(source));
            _source.Text = own ? Tr.T("set in this profile") : Tr.F("↳ from \"{0}\"", ProfileNames.Display(source));
            _toggle.Content = own ? Tr.T("Restore inherited") : Tr.T("Set here");
            _toggle.Visibility = own && vm.IsRoot ? Visibility.Collapsed : Visibility.Visible;
            if (!rebuild && _shownSet == own) return;
            _shownSet = own;
            _controls.Content = own
                ? ParameterEditors.Build(_id, vm.Set.Get(_id)!, vm.Conditions, vm.MaxBrightness, (change, again) =>
                {
                    try
                    {
                        vm.SetParameter(_id, change(vm.Set.Get(_id)!));
                        if (again) Refresh(rebuild: true);
                    }
                    catch (Exception e) { Log.Error("parameter edit", e); _w._errors.Text = Tr.F("Error: {0}", e.Message); }
                })
                : null;
            if (own && _id == ParameterId.Power && vm.Set.Power is { OwnPreset: true }) AddPresetButton();
        }

        // «Настроить пресет…»: LLT's GodMode window on this profile's own preset (it exists once the profile is saved
        // with an own preset).
        void AddPresetButton()
        {
            var vm = _w._vm;
            var saved = vm.Original.Set.Power is { OwnPreset: true } && ProfileNames.Display(vm.Original) == vm.Name;
            var panel = new StackPanel();
            var editors = (UIElement)_controls.Content;
            _controls.Content = null;                       // an element has one logical parent
            panel.Children.Add(editors);
            var button = new WpfUiButton { Content = Tr.T("Edit preset…"), Icon = SymbolRegular.Gauge24, IsEnabled = saved, Margin = new Thickness(0, 0, 0, 4) };
            button.Click += async (_, _) =>
            {
                try { await PresetEditor.EditAsync(_w, vm.Host, vm.Original); }
                catch (Exception e) { Log.Error("preset editor", e); _w._errors.Text = Tr.F("Error: {0}", e.Message); }
            };
            panel.Children.Add(button);
            if (!saved) panel.Children.Add(Text(Tr.T("Save the profile to edit its preset in the GodMode window."), "SubLine"));
            _controls.Content = panel;
        }
    }
}
