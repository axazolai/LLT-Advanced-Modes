using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Plugin.Pipelines;
using LegionModes.Plugin.Ui.ViewModels;
using LenovoLegionToolkit.Lib.Automation.Pipeline;
using LenovoLegionToolkit.WPF.Controls;
using LenovoLegionToolkit.WPF.Controls.Automation;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows;
using LenovoLegionToolkit.WPF.Windows.Automation;
using Wpf.Ui.Common;
using static LegionModes.Plugin.Ui.Look;
using Brushes = System.Windows.Media.Brushes;
using CardControl = LenovoLegionToolkit.WPF.Controls.Custom.CardControl;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using LltResource = LenovoLegionToolkit.WPF.Resources.Resource;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using WpfUiButton = Wpf.Ui.Controls.Button;
using WpfUiMenuItem = Wpf.Ui.Controls.MenuItem;

namespace LegionModes.Plugin.Ui;

// «Автоматизация» of one profile: its LLT pipelines grouped ВХОД, СОБЫТИЯ, ВЫХОД (accordion headers, numbering runs
// through the groups), each group in the order Resolver.Pipelines gives. Own pipelines are LLT's AutomationPipelineControl
// fed with StepCatalog, reshaped into the profile list's row; inherited ones are cards with their source, a switch and
// moving, never deleting. Rows move within their group only (a pipeline's group is its kind). Save writes the own
// pipelines and an order slot for every entry.
public sealed class AutomationWindow : BaseWindow
{
    enum Group { Enter, Events, Exit }

    static (Group Group, string Title)[] Groups => [(Group.Enter, Tr.T("ENTER")), (Group.Events, Tr.T("EVENTS")), (Group.Exit, Tr.T("EXIT"))];

    sealed class Entry
    {
        public required Guid Id;
        public required Profile Source;
        public required bool Own;
        public bool Enabled = true;
        public string Key = PipelineKeys.Enter;
        public Pipeline? Inherited;                       // inherited: the model pipeline
        public Profile? BlockedBy;                        // switched off higher up: shown at the end, greyed, not movable
        public AutomationPipelineControl? Control;        // own: LLT's editor
        public FrameworkElement? View;                    // the row, built once
        public TextBlock Overline = null!;
        public WpfUiButton Up = null!, Down = null!;
        public Wpf.Ui.Controls.ToggleSwitch Toggle = null!;
        public ContentControl Accessory = null!;          // LLT's «Настроить», moved to the right with the other tools
        public int Number;
        public Group Group => Key switch { PipelineKeys.Enter => Group.Enter, PipelineKeys.Exit => Group.Exit, _ => Group.Events };
    }

    readonly ProfilesViewModel _vm;
    readonly Profile _profile;
    readonly List<Entry> _entries = [];                  // always ordered by group, then by the order within the group
    readonly StackPanel _list = new() { Margin = new Thickness(16, 4, 16, 12) };
    readonly Dictionary<Group, StackPanel> _panels = [];
    readonly HashSet<Group> _folded = [];
    readonly ListDrag _drag;
    readonly TextBlock _title = new();
    readonly TextBlock _errors = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly WpfUiButton _save = new() { Content = Tr.T("Save"), Appearance = ControlAppearance.Primary, MinWidth = 120, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    StepSet? _steps;
    bool _dirty, _closingConfirmed;

    sealed record StepSet(LenovoLegionToolkit.Lib.Automation.Steps.IAutomationStep[] Steps, int Total);

    AutomationWindow(ProfilesViewModel vm, Profile p)
    {
        _vm = vm;
        _profile = p;
        Title = Tr.T("Automation");
        Width = 900;
        Height = 820;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(SharedResources.Instance);
        _errors.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        _title.Text = Tr.F("Automation · {0} · {1}", vm.Config.IndexOf(p), ProfileNames.Display(p));
        foreach (var (g, _) in Groups) _panels[g] = new StackPanel();

        var add = new WpfUiButton { Content = Tr.T("Add"), Icon = SymbolRegular.Add24, Appearance = ControlAppearance.Primary, VerticalAlignment = VerticalAlignment.Top };
        add.ContextMenu = new ContextMenu
        {
            Items =
            {
                Item(Tr.T("On entering the profile"), SymbolRegular.ArrowEnterLeft24, () => AddManual(PipelineKeys.Enter)),
                Item(Tr.T("On leaving the profile"), SymbolRegular.ArrowExportLtr24, () => AddManual(PipelineKeys.Exit)),
                Item(Tr.T("LLT event…"), SymbolRegular.Flash24, AddTriggered),
            },
        };
        add.Click += (_, _) => { add.ContextMenu.PlacementTarget = add; add.ContextMenu.IsOpen = true; };
        var top = new Grid { Margin = new Thickness(16, 0, 16, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Text(Tr.T("LLT actions this profile runs: on entering it, on leaving it and on LLT events while it is in force. ") +
                              Tr.T("Inherited ones can be turned off or reordered; what is turned off here does not reach profiles inheriting from this one. ") +
                              Tr.T("All enabled actions for one event run in list order."), "SubLine"));
        Grid.SetColumn(add, 1);
        top.Children.Add(add);

        // The description and «Добавить» stay; only the list scrolls (it keeps its own top margin).
        var scroll = new Wpf.Ui.Controls.DynamicScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(scroll, 1);
        body.Children.Add(top);
        body.Children.Add(scroll);
        _drag = new ListDrag(scroll);
        Content = Frame(SymbolRegular.Flowchart24, "PalettePurpleBrush", _title, body, Footer());
        Closing += OnClosing;
        Loaded += async (_, _) => await LoadAsync();
    }

    public static void Open(Window? owner, ProfilesViewModel vm, Profile p) =>
        new AutomationWindow(vm, p) { Owner = owner }.ShowDialog();

    UIElement Footer()
    {
        var footer = new Grid { Margin = new Thickness(16, 0, 16, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var effective = new WpfUiButton { Content = Tr.T("Effective actions"), Icon = SymbolRegular.Flowchart24, Margin = new Thickness(8, 0, 0, 0) };
        effective.Click += (_, _) => Safe(() => { if (Draft() is { } d) EffectiveActionsWindow.Open(this, d.Config, d.Profile); });
        var cancel = new WpfUiButton { Content = Tr.T("Cancel"), MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        _save.Click += async (_, _) => await SaveAsync();
        Grid.SetColumn(effective, 1);
        Grid.SetColumn(_save, 2);
        Grid.SetColumn(cancel, 3);
        footer.Children.Add(_errors);
        footer.Children.Add(effective);
        footer.Children.Add(_save);
        footer.Children.Add(cancel);
        return footer;
    }

    void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { Log.Error("automation window", e); _errors.Text = e.Message; }
    }

    async Task LoadAsync()
    {
        try
        {
            var (steps, total) = await StepCatalog.GetAsync();
            _steps = new StepSet(steps, total);
            foreach (var e in Resolver.Pipelines(_vm.Config, _profile))
            {
                var own = e.Source.Id == _profile.Id;
                var entry = new Entry
                {
                    Id = e.Pipeline.Id, Source = e.Source, Own = own, Enabled = e.State == PipelineState.Wins, Key = e.Pipeline.Key,
                    BlockedBy = e.State == PipelineState.Blocked ? e.BlockedBy : null,
                };
                if (own) entry.Control = MakeControl(PipelineJson.Read(e.Pipeline.Llt));
                else entry.Inherited = e.Pipeline;
                _entries.Add(entry);
            }
            SortByGroup();
            Render();
        }
        catch (Exception e)
        {
            Log.Error("automation load", e);
            _errors.Text = Tr.F("Could not load the actions: {0}", e.Message);
        }
    }

    // Stable: the order within a group stays; the blocked ones stay at its end.
    void SortByGroup()
    {
        var sorted = _entries.OrderBy(e => e.Group).ThenBy(e => e.BlockedBy is not null).ToList();
        _entries.Clear();
        _entries.AddRange(sorted);
    }

    AutomationPipelineControl MakeControl(AutomationPipeline pipeline)
    {
        var control = new AutomationPipelineControl(pipeline, _steps!.Steps, _steps.Total);
        control.OnChanged += (_, _) => Changed();
        // LLT's drag handles (a step's, and the pipeline's own in its header) start this window's drag instead of
        // LLT's DragDrop: the press is taken on the way down, before LLT's handler sees it.
        control.PreviewMouseLeftButtonDown += (_, e) => Safe(() => OnHandlePress(control, e));
        control.OnDelete += (_, _) => Safe(() =>
        {
            _entries.RemoveAll(x => x.Control == control);
            Changed();
            Render();
        });
        return control;
    }

    void OnHandlePress(AutomationPipelineControl control, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || Up<SymbolIcon>(source) is not { Symbol: SymbolRegular.ReOrderDotsVertical24 } handle) return;
        if (Up<AbstractAutomationStepControl>(handle) is { Parent: Panel steps } step && Up<AutomationPipelineControl>(step) == control)
        {
            _drag.Begin(steps, step, handle, e, _ => Changed());     // LLT reads the step order from its panel on save
            return;
        }
        if (_entries.FirstOrDefault(x => x.Control == control) is { } entry) BeginEntryDrag(entry, handle, e);
    }

    static T? Up<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x is T hit) return hit;
        return null;
    }

    // Within the entry's group: `to` is the index among that group's rows.
    void BeginEntryDrag(Entry entry, UIElement capture, MouseButtonEventArgs e) =>
        _drag.Begin(_panels[entry.Group], entry.View!, capture, e, to =>
        {
            var group = _entries.Where(x => x.Group == entry.Group).ToList();
            group.Remove(entry);
            group.Insert(to, entry);
            var first = _entries.FindIndex(x => x.Group == entry.Group);
            _entries.RemoveAll(x => x.Group == entry.Group);
            _entries.InsertRange(first, group);
            SortByGroup();
            Changed();
            Render();
        });

    // The logical tree: the control builds its parts in code, and a collapsed expander's content has no visuals yet.
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T hit) yield return hit;
            foreach (var x in Descendants<T>(child)) yield return x;
        }
    }

    void Render()
    {
        _list.Children.Clear();
        foreach (var p in _panels.Values) p.Children.Clear();
        if (_entries.Count == 0)
        {
            _list.Children.Add(Text(Tr.T("No actions. \"Add\" creates an action on enter, on exit or on an LLT event."), "SubLine"));
            return;
        }
        var number = 0;
        foreach (var (group, title) in Groups)
        {
            var rows = _entries.Where(e => e.Group == group).ToList();
            if (rows.Count == 0) continue;
            var panel = _panels[group];
            _list.Children.Add(GroupHeader(group, title, rows.Count));
            _list.Children.Add(panel);
            panel.Visibility = _folded.Contains(group) ? Visibility.Collapsed : Visibility.Visible;
            for (var i = 0; i < rows.Count; i++)
            {
                var e = rows[i];
                e.View ??= Row(e);
                (e.View.Parent as Panel)?.Children.Remove(e.View);
                panel.Children.Add(e.View);
                e.Number = e.BlockedBy is null ? ++number : 0;      // a row blocked from above runs nowhere here: no number
                Overline(e);
                var movable = rows.Count(x => x.BlockedBy is null);
                e.Up.IsEnabled = e.BlockedBy is null && i > 0;
                e.Down.IsEnabled = e.BlockedBy is null && i < movable - 1;
                e.View.Opacity = e.Enabled ? 1 : 0.55;
            }
        }
    }

    // «2. по событию LLT · все условия (И)»: a composite trigger's logic stands out in the accent colour.
    void Overline(Entry e)
    {
        e.Overline.Inlines.Clear();
        e.Overline.Inlines.Add(new System.Windows.Documents.Run((e.Number > 0 ? $"{e.Number}. " : "") + ProfileEditorWindow.KeyText(e.Key)));
        var logic = Logic(e);
        if (logic is not null)
        {
            e.Overline.Inlines.Add(new System.Windows.Documents.Run(" · "));
            var run = new System.Windows.Documents.Run(logic) { FontWeight = FontWeights.SemiBold };
            run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "SystemAccentColorSecondaryBrush");
            e.Overline.Inlines.Add(run);
        }
        e.Overline.Inlines.Add(new System.Windows.Documents.Run(e.Own ? "" : Tr.F(" · ↳ from \"{0}\"", ProfileNames.Display(e.Source))));
    }

    static string? Logic(Entry e)
    {
        try
        {
            var trigger = e.Control is { } c ? c.AutomationPipeline.Trigger : e.Inherited is { } p ? PipelineJson.Read(p.Llt).Trigger : null;
            return trigger switch
            {
                LenovoLegionToolkit.Lib.Automation.Pipeline.Triggers.AndAutomationPipelineTrigger => Tr.T("all conditions (AND)"),
                LenovoLegionToolkit.Lib.Automation.Pipeline.Triggers.OrAutomationPipelineTrigger => Tr.T("any condition (OR)"),
                _ => null,
            };
        }
        catch (Exception ex) { Log.Trace("pipeline logic: " + ex.Message); return null; }
    }

    // "——— ВХОД ———": a solid line each side, a larger bold title in the sub-line's colour; a click folds the group.
    UIElement GroupHeader(Group group, string title, int count)
    {
        var folded = _folded.Contains(group);
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 10), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = folded ? Tr.T("Expand") : Tr.T("Collapse") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = folded ? $"{title} · {count}" : title, FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(text, 1);
        grid.Children.Add(Rule(0));
        grid.Children.Add(text);
        grid.Children.Add(Rule(2));
        grid.MouseLeftButtonUp += (_, _) => Safe(() =>
        {
            if (!_folded.Remove(group)) _folded.Add(group);
            Render();
        });
        return grid;
    }

    static Rectangle Rule(int column)
    {
        var r = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        r.SetResourceReference(Shape.FillProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(r, column);
        return r;
    }

    // One row, built once, in the profile list's shape: drag handle, icon, the over-line («1. по событию LLT · свой») above
    // the title, then «Настроить» (LLT's, for a pipeline with an event), ↑ ↓ and the run switch on the right. An own
    // pipeline is LLT's control with its header rebuilt (DressAsync); an inherited one is a card.
    FrameworkElement Row(Entry e)
    {
        e.Overline = Text("", "SubLine", new Thickness(0, 0, 0, 2));
        e.Up = Small(SymbolRegular.ArrowUp24, Tr.T("Up"), true, () => Move(e, -1));
        e.Down = Small(SymbolRegular.ArrowDown24, Tr.T("Down"), true, () => Move(e, 1));
        e.Accessory = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        e.Toggle = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = e.Enabled, Margin = new Thickness(12, 0, 0, 0), ToolTip = Tr.T("Run in this profile"), VerticalAlignment = VerticalAlignment.Center };
        e.Toggle.Click += (_, _) => Safe(() =>
        {
            e.Enabled = e.Toggle.IsChecked == true;
            e.View!.Opacity = e.Enabled ? 1 : 0.55;
            Changed();
        });

        if (e.Control is { } c)
        {
            // Buttons inside the expander's header toggle collapse to an empty 14×14 box, so the tools lie over the row
            // instead, lined up with an empty slot the header keeps for them.
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(c);
            var tools = Tools(e);
            tools.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            tools.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(tools);
            _ = DressAsync(e, c, row, tools);
            return row;
        }
        var header = new CardHeaderControl
        {
            Title = ProfileEditorWindow.PipelineName(e.Inherited!),
            Subtitle = e.BlockedBy is { } by
                ? Tr.F("Turned off in \"{0}\"; it can be turned on in its automation", ProfileNames.Display(by))
                : Tr.F("Inherited from \"{0}\"; it can be changed in its automation", ProfileNames.Display(e.Source)),
        };
        if (e.BlockedBy is not null)
        {
            e.Up.Visibility = e.Down.Visibility = e.Toggle.Visibility = Visibility.Collapsed;
            e.Enabled = false;
        }
        return new CardControl { Header = Wrap(e, SymbolRegular.Flowchart24, header, Tools(e)), Margin = new Thickness(0, 0, 0, 8) };
    }

    StackPanel Tools(Entry e)
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        tools.Children.Add(e.Accessory);
        tools.Children.Add(e.Up);
        tools.Children.Add(e.Down);
        tools.Children.Add(e.Toggle);
        return tools;
    }

    // Handle (twice the profile list's height), icon, over-line + title, and on the right `right` (the tools, or an empty
    // slot of their size when they lie over the row).
    UIElement Wrap(Entry e, SymbolRegular icon, UIElement header, FrameworkElement right)
    {
        var handle = new Border { Width = 24, Margin = new Thickness(-6, -8, 6, -8), Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = Tr.T("Drag to change the order") };
        handle.Child = Dots(6);
        if (e.BlockedBy is not null) { handle.Child.Visibility = Visibility.Hidden; handle.Cursor = null; handle.ToolTip = null; handle.IsHitTestVisible = false; }
        handle.PreviewMouseLeftButtonDown += (_, ev) => Safe(() => BeginEntryDrag(e, handle, ev));

        var glyph = new SymbolIcon { Symbol = icon, FontSize = 24, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(e.Overline);
        center.Children.Add(header);
        right.Margin = new Thickness(12, 0, 0, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(glyph, 1);
        Grid.SetColumn(center, 2);
        Grid.SetColumn(right, 3);
        grid.Children.Add(handle);
        grid.Children.Add(glyph);
        grid.Children.Add(center);
        grid.Children.Add(right);
        return grid;
    }

    // A drag handle of 2 × `rows` dots at one pitch both ways (the size and spacing of the 16 px dot glyph).
    static FrameworkElement Dots(int rows)
    {
        const double size = 2.5, pitch = 4.5;
        var canvas = new Canvas { Width = pitch + size, Height = (rows - 1) * pitch + size, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < 2; c++)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = size, Height = size };
                dot.SetResourceReference(Shape.FillProperty, "TextFillColorTertiaryBrush");
                Canvas.SetLeft(dot, c * pitch);
                Canvas.SetTop(dot, r * pitch);
                canvas.Children.Add(dot);
            }
        return canvas;
    }

    // LLT's pipeline control, reshaped: its header goes into this row's shape, the expander's own icon slot and chevron
    // go (a click anywhere on the header still opens and closes it: the header is the expander's toggle), LLT's header
    // drag handle and its two boxes (Exclusive, Run on startup: the plugin ignores both) are hidden, its «Настроить»
    // moves to the tools, several triggers are listed alphabetically, and the step handles grow 1.5×.
    async Task DressAsync(Entry e, AutomationPipelineControl c, Grid row, FrameworkElement tools)
    {
        try
        {
            await c.InitializedTask;
            if (Descendants<Wpf.Ui.Controls.CardExpander>(c).FirstOrDefault() is not { Header: UIElement header } expander) return;
            Sync(e, c, header);
            c.OnChanged += (_, _) => Safe(() => Sync(e, c, header));      // LLT rebuilds the accessory and the title on changes
            var icon = expander.Icon;
            var slot = new Border { VerticalAlignment = VerticalAlignment.Center };
            expander.Header = null;                                        // an element has one logical parent
            expander.Header = Wrap(e, icon, header, slot);
            expander.Icon = SymbolRegular.Empty;
            // Keep the tools over the slot, the slot as wide as the tools (the header grows when the title wraps or the
            // row opens; «Настроить» comes and goes with the trigger).
            row.LayoutUpdated += (_, _) => Safe(() =>
            {
                if (slot.Width != tools.ActualWidth) slot.Width = tools.ActualWidth;
                if (slot.Height != tools.ActualHeight) slot.Height = tools.ActualHeight;
                if (!slot.IsVisible || !row.IsVisible) return;
                var at = slot.TranslatePoint(new Point(0, 0), row);
                var margin = new Thickness(0, at.Y, Math.Max(0, row.ActualWidth - at.X - slot.ActualWidth), 0);
                if (tools.Margin != margin) tools.Margin = margin;
            });
            void HideParts()
            {
                foreach (var part in VisualDescendants(expander).OfType<FrameworkElement>().Where(x => x.Name is "ChevronGrid" or "ControlIcon"))
                    part.Visibility = Visibility.Collapsed;
            }
            expander.Loaded += (_, _) => HideParts();
            _ = expander.Dispatcher.BeginInvoke(HideParts, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex) { Log.Error("pipeline row", ex); }
    }

    void Sync(Entry e, AutomationPipelineControl c, UIElement header)
    {
        if (e.Number > 0) Overline(e);                     // the trigger may have become И / ИЛИ through «Настроить»
        foreach (var box in Descendants<CheckBox>(c).Where(b => Equals(b.Content, LltResource.AutomationPipelineControl_Exclusive)
                                                               || Equals(b.Content, LltResource.AutomationPipelineControl_RunOnStartup)))
            box.Visibility = Visibility.Collapsed;
        foreach (var step in Descendants<AbstractAutomationStepControl>(c))
            foreach (var dots in Descendants<SymbolIcon>(step).Where(s => s.Symbol == SymbolRegular.ReOrderDotsVertical24 && s.LayoutTransform == Transform.Identity))
                dots.LayoutTransform = new ScaleTransform(1.5, 1.5);   // 1.5× in both directions: no stretched dots
        if (header is not CardHeaderControl h) return;
        if (h.Accessory is { } accessory)
        {
            h.Accessory = null;
            foreach (var dots in Descendants<SymbolIcon>(accessory).Where(s => s.Symbol == SymbolRegular.ReOrderDotsVertical24))
                dots.Visibility = Visibility.Collapsed;
            e.Accessory.Content = accessory;
        }
        if (h.Title?.Contains('\n') == true)
        {
            var sorted = string.Join(Environment.NewLine, h.Title.Split('\n').Select(x => x.TrimEnd('\r')).OrderBy(x => x, StringComparer.CurrentCulture));
            if (sorted != h.Title) h.Title = sorted;
        }
    }

    static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var x in VisualDescendants(child)) yield return x;
        }
    }

    static WpfUiButton Small(SymbolRegular icon, string tip, bool enabled, Action action)
    {
        var b = new WpfUiButton { Icon = icon, Appearance = ControlAppearance.Transparent, ToolTip = tip, IsEnabled = enabled, Padding = new Thickness(6), Margin = new Thickness(4, 0, 0, 0) };
        b.Click += (_, _) => action();
        return b;
    }

    static WpfUiMenuItem Item(string text, SymbolRegular icon, Action action)
    {
        var item = new WpfUiMenuItem { Header = text, SymbolIcon = icon };
        item.Click += (_, _) => action();
        return item;
    }

    // ↑ ↓ within the group.
    void Move(Entry e, int step) => Safe(() =>
    {
        var group = _entries.Where(x => x.Group == e.Group && x.BlockedBy is null).ToList();
        var i = group.IndexOf(e);
        var j = i + step;
        if (j < 0 || j >= group.Count) return;
        var a = _entries.IndexOf(group[i]);
        var b = _entries.IndexOf(group[j]);
        (_entries[a], _entries[b]) = (_entries[b], _entries[a]);
        Changed();
        Render();
    });

    // New pipelines go to the end of their group.
    void Append(Entry e)
    {
        _entries.Add(e);
        SortByGroup();
        _folded.Remove(e.Group);
        Changed();
        Render();
    }

    void AddManual(string key) => Safe(() =>
    {
        if (_steps is null) return;
        var pipeline = new AutomationPipeline(key == PipelineKeys.Enter ? "On enter" : "On leave");
        Append(new Entry { Id = pipeline.Id, Source = _profile, Own = true, Key = key, Control = MakeControl(pipeline) });
    });

    // LLT's "Create Action" window; the chosen trigger starts a new own pipeline.
    void AddTriggered() => Safe(() =>
    {
        if (_steps is null) return;
        var window = new CreateAutomationPipelineWindow([], trigger => Safe(() =>
        {
            var pipeline = new AutomationPipeline(trigger);
            Append(new Entry { Id = pipeline.Id, Source = _profile, Own = true, Key = PipelineJson.KeyOf(trigger), Control = MakeControl(pipeline) });
        })) { Owner = this };
        window.ShowDialog();
    });

    void Changed()
    {
        _dirty = true;
        _save.IsEnabled = true;
    }

    // The profile with its own pipelines and an order slot per entry; the config with it in place.
    (ModesConfig Config, Profile Profile)? Draft()
    {
        var own = new List<Pipeline>();
        foreach (var e in _entries.Where(x => x.Own && x.Control is not null))
        {
            var llt = e.Control!.CreateAutomationPipeline();
            llt.RunOnStartup = false;                     // hidden: the profile's «вход» covers the start (owner 2026-10-20)
            var key = llt.Trigger is { } t ? PipelineJson.KeyOf(t) : e.Key is PipelineKeys.Enter or PipelineKeys.Exit ? e.Key : PipelineKeys.Enter;
            own.Add(new Pipeline(llt.Id, key, PipelineJson.Write(llt)));
        }
        var shown = _entries.Where(e => e.BlockedBy is null).Select(e => new PipelineSlot(e.Id, e.Enabled));
        var profile = _profile with { Pipelines = own, Order = Resolver.KeepBlockedSlots(_vm.Config, _profile, shown) };
        var config = _vm.Config with { Profiles = _vm.Config.Profiles.Select(x => x.Id == _profile.Id ? profile : x).ToList() };
        return (config, profile);
    }

    async Task SaveAsync()
    {
        try
        {
            if (Draft() is not { } d) return;
            var error = await _vm.TrySaveAsync(d.Config, Tr.F("Automation \"{0}\" saved", ProfileNames.Display(_profile)));
            if (error is not null) { _errors.Text = error; return; }
            _closingConfirmed = true;
            Close();
        }
        catch (Exception e)
        {
            Log.Error("automation save", e);
            _errors.Text = Tr.F("Not saved: {0}", e.Message);
        }
    }

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed || !_dirty) return;
        e.Cancel = true;
        try
        {
            if (!await MessageBoxHelper.ShowAsync(this, Tr.T("Close without saving?"), Tr.T("The automation changes will be lost."), Tr.T("Close"), Tr.T("Stay"))) return;
            _closingConfirmed = true;
            Close();
        }
        catch (Exception ex) { Log.Error("automation closing", ex); }
    }
}
