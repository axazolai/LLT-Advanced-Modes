using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;
using LegionModes.Plugin.Ui.ViewModels;
using LenovoLegionToolkit.WPF.Controls;
using Wpf.Ui.Common;
using CardControl = LenovoLegionToolkit.WPF.Controls.Custom.CardControl;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using UserControl = System.Windows.Controls.UserControl;
using WpfUiButton = Wpf.Ui.Controls.Button;
using WpfUiMenuItem = Wpf.Ui.Controls.MenuItem;

namespace LegionModes.Plugin.Ui;

// The profile list as LLT's automation list draws it: one CardControl per profile, built in code because
// CardHeaderControl's Title and Subtitle are plain properties. Between cards a Gap: it opens on hover (insert a new
// profile there) and, while a card is dragged by its handle, at the drop position with «Вставить сюда».
public partial class ProfilesTab : UserControl
{
    const double EdgeZone = 48, ScrollStep = 8;          // auto-scroll: 8 px per 30 ms (~270 px/s) within 48 px of an edge

    ProfilesViewModel? _vm;
    readonly List<(Profile Profile, FrameworkElement Card)> _cards = [];
    readonly List<Gap> _gaps = [];                       // _gaps[k - 1] is the space before card k
    readonly System.Windows.Threading.DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    Drag? _drag;

    sealed class Drag
    {
        public required Profile Profile;
        public required FrameworkElement Card;
        public required FrameworkElement Handle;
        public required Ghost Ghost;
        public int Slot = -1;                            // the gap index under the mouse, -1 = none (or no change)
        public double ScrollDirection;
    }

    public ProfilesTab()
    {
        Resources.MergedDictionaries.Add(SharedResources.Instance);   // before InitializeComponent: StaticResource is resolved while loading
        InitializeComponent();
        _title.Text = Tr.T("Profiles");
        _hint.Text = Tr.T("The most specific matching profile applies; on equal specificity, the one higher in the list. Whatever a profile leaves unset is taken from the more general ones.");
        _sortButton.Content = Tr.T("Sort by hierarchy");
        _sortButton.ToolTip = Tr.T("Puts every profile under its parent; the order of siblings stays");
        _sortButton.Click += (_, _) => Guard("sort profiles", () => _vm?.SortByHierarchyAsync() ?? Task.CompletedTask);
        _addButton.Content = Tr.T("New profile");
        _addButton.Click += (_, _) => Guard("new profile", AddAsync);
        _scrollTimer.Tick += (_, _) => Safe("drag scroll", ScrollTick);
    }

    public void Bind(ProfilesViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        vm.ListChanged += RequestRender;
        vm.Document.ModesChanged += RequestRender;
        Render();
    }

    // A save raises both events: they make one redraw, after the dispatcher is done with the save.
    bool _renderPending;

    void RequestRender()
    {
        if (_renderPending) return;
        _renderPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _renderPending = false;
            Safe("profiles render", Render);
        });
    }

    // The profile in force changed: only the marks move.
    public void RefreshInForce() { if (_drag is null) Render(); }

    static async void Guard(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { Log.Error(what, e); }
    }

    static void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Error(what, e); }
    }

    void Render()
    {
        if (_vm is null) return;
        _list.Children.Clear();
        _cards.Clear();
        _gaps.Clear();
        var profiles = _vm.Config.Profiles;
        for (var i = 0; i < profiles.Count; i++)
        {
            var card = Card(_vm, profiles[i]);
            _cards.Add((profiles[i], card));
            _list.Children.Add(Row(profiles[i], card));
            if (i == profiles.Count - 1) break;
            var at = i + 1;
            var gap = new Gap(() => Guard("insert profile", () => InsertAsync(at)));
            card.MouseEnter += (_, _) => gap.SetCardHover(true);
            card.MouseLeave += (_, _) => gap.SetCardHover(false);
            _gaps.Add(gap);
            _list.Children.Add(gap);
        }
    }

    // The card with the dots of its level in the gutter to the left of it.
    static FrameworkElement Row(Profile p, FrameworkElement card)
    {
        if (Texts.Level(p) == 0) return card;
        // One dot per level, each centred in its own 16 px column, so the dots of every row stand on the same vertical lines.
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Center, IsHitTestVisible = false,
            Margin = new Thickness(Texts.LevelDotShift, 0, 0, 0),
        };
        for (var i = 0; i < Texts.Level(p); i++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            dot.SetResourceReference(Shape.FillProperty, "TextFillColorSecondaryBrush");
            dots.Children.Add(new Grid { Width = Texts.LevelStep, Children = { dot } });
        }
        var row = new Grid();
        row.Children.Add(card);
        row.Children.Add(dots);
        return row;
    }

    // ---- drag and drop ----

    void BeginDrag(Profile p, FrameworkElement card, FrameworkElement handle, MouseButtonEventArgs e)
    {
        if (_vm is null || p.Kind != ProfileKind.Automatic) return;
        var layer = AdornerLayer.GetAdornerLayer(_list);
        if (layer is null) return;
        var ghost = new Ghost(_list, card, e.GetPosition(card).Y);
        layer.Add(ghost);
        _drag = new Drag { Profile = p, Card = card, Handle = handle, Ghost = ghost };
        card.Opacity = 0.35;
        handle.CaptureMouse();
        handle.MouseMove += OnDragMove;
        handle.MouseLeftButtonUp += OnDragUp;
        handle.LostMouseCapture += OnDragLost;
        _scrollTimer.Start();
        UpdateDrag(e.GetPosition(_list));
        e.Handled = true;
    }

    void OnDragMove(object sender, MouseEventArgs e) => Safe("drag move", () => UpdateDrag(e.GetPosition(_list)));

    void OnDragUp(object sender, MouseButtonEventArgs e) => Safe("drop", () => EndDrag(drop: true));

    void OnDragLost(object sender, MouseEventArgs e) => Safe("drag lost", () => EndDrag(drop: false));

    void UpdateDrag(Point inList)
    {
        if (_drag is null || _vm is null) return;
        _drag.Ghost.MoveTo(inList.Y);

        // The slot: how many cards (the dragged one excluded) have their middle above the mouse, kept between the root
        // and «Автономия»; the dragged card's own neighbours mean "no change".
        var dragged = _cards.FindIndex(c => c.Profile.Id == _drag.Profile.Id);
        var manual = _cards.FindIndex(c => c.Profile.Kind == ProfileKind.Manual);
        var above = 0;
        for (var i = 0; i < _cards.Count; i++)
        {
            if (i == dragged) continue;
            var top = _cards[i].Card.TranslatePoint(new Point(0, 0), _list).Y;
            if (top + _cards[i].Card.ActualHeight / 2 < inList.Y) above++;
        }
        var target = Math.Clamp(above, 1, manual - 1);             // index in the list without the dragged card
        var slot = target >= dragged ? target + 1 : target;         // the gap (index in the full list) it lands at
        if (target == dragged) slot = -1;
        if (slot != _drag.Slot)
        {
            _drag.Slot = slot;
            for (var k = 0; k < _gaps.Count; k++) _gaps[k].SetDropTarget(k + 1 == slot);
        }

        var inView = _list.TranslatePoint(inList, _scroll);
        _drag.ScrollDirection = inView.Y < EdgeZone ? -1 : inView.Y > _scroll.ActualHeight - EdgeZone ? 1 : 0;
    }

    void ScrollTick()
    {
        if (_drag is null || _drag.ScrollDirection == 0) return;
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + _drag.ScrollDirection * ScrollStep);
        UpdateDrag(Mouse.GetPosition(_list));
    }

    void EndDrag(bool drop)
    {
        if (_drag is not { } d) return;
        _drag = null;
        _scrollTimer.Stop();
        d.Handle.MouseMove -= OnDragMove;
        d.Handle.MouseLeftButtonUp -= OnDragUp;
        d.Handle.LostMouseCapture -= OnDragLost;
        if (d.Handle.IsMouseCaptured) d.Handle.ReleaseMouseCapture();
        AdornerLayer.GetAdornerLayer(_list)?.Remove(d.Ghost);
        d.Card.Opacity = 1;
        foreach (var g in _gaps) g.SetDropTarget(false);
        if (drop && d.Slot > 0 && _vm is { } vm)
        {
            var dragged = _cards.FindIndex(c => c.Profile.Id == d.Profile.Id);
            var to = d.Slot > dragged ? d.Slot - 1 : d.Slot;
            Guard("drop profile", () => vm.MoveToAsync(d.Profile, to));
        }
    }

    // The dragged card drawn under the mouse, over the list.
    sealed class Ghost : Adorner
    {
        readonly ImageBrush _brush;
        readonly Size _size;
        readonly double _grab;
        readonly double _x;
        double _y;

        public Ghost(UIElement list, FrameworkElement card, double grabY) : base(list)
        {
            _brush = new ImageBrush(BoardDrag.Snapshot(card)) { Opacity = 0.85 };   // taken before the card dims
            _size = new Size(card.ActualWidth, card.ActualHeight);
            _x = card.TranslatePoint(new Point(0, 0), list).X;
            _grab = grabY;
            IsHitTestVisible = false;
        }

        public void MoveTo(double y) { _y = y; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc) =>
            dc.DrawRectangle(_brush, null, new Rect(new Point(_x, _y - _grab), _size));
    }

    // ---- the space between cards ----

    // 8 at rest, 16 while the card above is hovered, 36 with a thin «+ Профиль сюда» button while the space itself is
    // hovered (inserts a new profile here); 44 with a sparse dashed «Вставить сюда» line while it is the drop position.
    sealed class Gap : Grid
    {
        const double Rest = 8, CardHover = 16, Hover = 36, DropHeight = 44;
        static readonly Duration Time = TimeSpan.FromMilliseconds(120);
        readonly WpfUiButton _add;
        readonly Grid _dropLine;
        bool _cardHover, _drop;

        public Gap(Action add)
        {
            Height = Rest;
            Background = Brushes.Transparent;                       // hit-testable when empty
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Add24, FontSize = 12, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = Tr.T("Profile here"), FontSize = 12, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            _add = new WpfUiButton
            {
                Content = content, Height = 24, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Opacity = 0, IsHitTestVisible = false, ToolTip = Tr.T("New profile at this position"),
            };
            _add.Click += (_, _) => add();
            _dropLine = DropLine();
            Children.Add(_dropLine);
            Children.Add(_add);
            MouseEnter += (_, _) => Update();
            MouseLeave += (_, _) => Update();
        }

        static Grid DropLine()
        {
            var grid = new Grid { Opacity = 0, IsHitTestVisible = false, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var text = new TextBlock { Text = Tr.T("Insert here"), FontSize = 12, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "SystemAccentColorSecondaryBrush");
            Grid.SetColumn(text, 1);
            grid.Children.Add(Dash(0));
            grid.Children.Add(text);
            grid.Children.Add(Dash(2));
            return grid;
        }

        static Line Dash(int column)
        {
            var line = new Line { X1 = 0, X2 = 1, Stretch = Stretch.Fill, StrokeThickness = 1.5, StrokeDashArray = [2, 6], VerticalAlignment = System.Windows.VerticalAlignment.Center };
            line.SetResourceReference(Shape.StrokeProperty, "SystemAccentColorSecondaryBrush");
            Grid.SetColumn(line, column);
            return line;
        }

        public void SetCardHover(bool on) { _cardHover = on; Update(); }

        public void SetDropTarget(bool on) { _drop = on; Update(); }

        void Update()
        {
            var hover = IsMouseOver && !_drop;
            var height = _drop ? DropHeight : hover ? Hover : _cardHover ? CardHover : Rest;
            BeginAnimation(HeightProperty, new DoubleAnimation(height, Time) { EasingFunction = new QuadraticEase() });
            _add.IsHitTestVisible = hover;
            _add.BeginAnimation(OpacityProperty, new DoubleAnimation(hover ? 1 : 0, Time));
            _dropLine.BeginAnimation(OpacityProperty, new DoubleAnimation(_drop ? 1 : 0, Time));
        }
    }

    // ---- one card ----

    FrameworkElement Card(ProfilesViewModel vm, Profile p)
    {
        var header = new CardHeaderControl { Title = $"{vm.Config.IndexOf(p)} · {ProfileNames.Display(p)}", Subtitle = Texts.ProfileSummary(p, Resolver.Parents(vm.Config, p)) };
        if (vm.InForce == p.Id) header.Success = Tr.T("In force now");

        // Handle, then the icon (CardControl's own icon slot sits left of the header, so both are drawn here instead,
        // at its size and margin: 24, 0,0,14,0).
        var lead = new Grid();
        lead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var handle = new Border
        {
            Width = 24, Margin = new Thickness(-6, -8, 6, -8), Background = Brushes.Transparent,
            Cursor = p.Kind == ProfileKind.Automatic ? Cursors.SizeAll : null,
            ToolTip = p.Kind == ProfileKind.Automatic ? Tr.T("Drag to change the order") : null,
        };
        var dots = new SymbolIcon { Symbol = SymbolRegular.ReOrderDotsVertical24, FontSize = 16, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        dots.SetResourceReference(Control.ForegroundProperty, "TextFillColorTertiaryBrush");
        if (p.Kind != ProfileKind.Automatic)
        {
            dots.Symbol = SymbolRegular.LockClosed24;   // the root and «Автономия» keep their place
            handle.ToolTip = Tr.T("The position of this profile is fixed");
        }
        handle.Child = dots;
        var icon = new SymbolIcon { Symbol = Texts.ProfileIcon(p), FontSize = 24, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center };
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(header, 2);
        lead.Children.Add(handle);
        lead.Children.Add(icon);
        lead.Children.Add(header);

        var accessory = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        if (p.Kind == ProfileKind.Automatic)
        {
            accessory.Children.Add(IconButton(SymbolRegular.ArrowUp24, Tr.T("Up"), vm.CanMove(p, -1), () => vm.MoveAsync(p, -1)));
            accessory.Children.Add(IconButton(SymbolRegular.ArrowDown24, Tr.T("Down"), vm.CanMove(p, 1), () => vm.MoveAsync(p, 1)));
        }
        var automation = new WpfUiButton { Content = Tr.T("Automation"), Margin = new Thickness(8, 0, 0, 0) };
        automation.Click += (_, e) => { e.Handled = true; Guard("open automation", () => AutomationAsync(vm, p)); };
        accessory.Children.Add(automation);
        var settings = IconButton(SymbolRegular.Settings24, Tr.T("Configure profile"), true, () => EditAsync(vm, p));
        settings.Appearance = ControlAppearance.Secondary;
        settings.Margin = new Thickness(8, 0, 0, 0);
        accessory.Children.Add(settings);

        // A click on the row itself opens nothing (owner): only its buttons act.
        var card = new CardControl { Header = lead, Content = accessory, Margin = new Thickness(Texts.LevelIndent(p), 0, 0, 0) };
        if (p.Kind == ProfileKind.Automatic)
        {
            handle.PreviewMouseLeftButtonDown += (_, e) => Safe("drag start", () => BeginDrag(p, card, handle, e));
            // As LLT's automation list: the rest of the commands on a right click.
            card.ContextMenu = new ContextMenu
            {
                Items =
                {
                    MenuItem(Tr.T("Up"), SymbolRegular.ArrowUp24, () => vm.MoveAsync(p, -1), vm.CanMove(p, -1)),
                    MenuItem(Tr.T("Down"), SymbolRegular.ArrowDown24, () => vm.MoveAsync(p, 1), vm.CanMove(p, 1)),
                    MenuItem(Tr.T("Copy…"), SymbolRegular.Copy24, () => CopyAsync(vm, p)),
                    MenuItem(Tr.T("Rename…"), SymbolRegular.Rename24, () => vm.RenameAsync(p)),
                    MenuItem(Tr.T("Delete"), SymbolRegular.Delete24, () => vm.DeleteAsync(p)),
                },
            };
        }
        return card;
    }

    static WpfUiButton IconButton(SymbolRegular icon, string tip, bool enabled, Func<Task> action)
    {
        var b = new WpfUiButton
        {
            Icon = icon, Appearance = ControlAppearance.Transparent, ToolTip = tip, IsEnabled = enabled,
            Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6),
        };
        b.Click += (_, e) => { e.Handled = true; Guard(tip, action); };
        return b;
    }

    static WpfUiMenuItem MenuItem(string text, SymbolRegular icon, Func<Task> action, bool enabled = true)
    {
        var item = new WpfUiMenuItem { Header = text, SymbolIcon = icon, IsEnabled = enabled };
        item.Click += (_, _) => Guard(text, action);
        return item;
    }

    async Task AddAsync()
    {
        if (_vm is null) return;
        var conditions = await ConditionWindow.AskAsync(Window.GetWindow(this), _vm, null, null);
        if (conditions is not null) await _vm.AddAsync(conditions);
    }

    async Task InsertAsync(int at)
    {
        if (_vm is null) return;
        var conditions = await ConditionWindow.AskAsync(Window.GetWindow(this), _vm, null, null);
        if (conditions is not null) await _vm.AddAsync(conditions, at);
    }

    async Task CopyAsync(ProfilesViewModel vm, Profile p)
    {
        var conditions = await ConditionWindow.AskAsync(Window.GetWindow(this), vm, p.Conditions, null);
        if (conditions is not null) await vm.CopyAsync(p, conditions);
    }

    Task EditAsync(ProfilesViewModel vm, Profile p)
    {
        ProfileEditorWindow.Open(Window.GetWindow(this), vm, p);
        return Task.CompletedTask;
    }

    Task AutomationAsync(ProfilesViewModel vm, Profile p)
    {
        AutomationWindow.Open(Window.GetWindow(this), vm, p);
        return Task.CompletedTask;
    }
}
