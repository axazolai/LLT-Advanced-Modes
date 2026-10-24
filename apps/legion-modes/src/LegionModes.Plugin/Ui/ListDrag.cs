using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using LegionModes.Core;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace LegionModes.Plugin.Ui;

// Reordering the items of one vertical panel as the profile list does: the dragged item dims and its picture follows
// the mouse, the drop place opens (the items below slide apart) with a sparse dashed «Вставить сюда» line, and the scroll
// viewer scrolls near its top or bottom edge. Used for the automation window's pipelines and for the steps inside a
// pipeline (LLT's own step panel).
internal sealed class ListDrag
{
    const double EdgeZone = 48, ScrollStep = 8, DropHeight = 44;   // the profile list's speed: 8 px per 30 ms
    static readonly Duration Time = TimeSpan.FromMilliseconds(120);
    const string GapTag = "list-drop-gap";

    readonly ScrollViewer _scroll;
    readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    State? _s;

    sealed class State
    {
        public required Panel Panel;
        public required FrameworkElement Item;
        public required UIElement Capture;
        public required Ghost Ghost;
        public required Action<int> Dropped;
        public Grid? Gap;
        public int Target = -1;                   // index among the other items; -1 = where it was
        public double ScrollDirection;
    }

    public ListDrag(ScrollViewer scroll)
    {
        _scroll = scroll;
        _timer.Tick += (_, _) => Safe(ScrollTick);
    }

    static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { Log.Error("list drag", e); }
    }

    // `dropped(index)`: the item's new index among the panel's items (gaps not counted), called only when it moved.
    public void Begin(Panel panel, FrameworkElement item, UIElement capture, MouseButtonEventArgs e, Action<int> dropped)
    {
        if (_s is not null || AdornerLayer.GetAdornerLayer(panel) is not { } layer) return;
        var ghost = new Ghost(panel, item, e.GetPosition(item));
        layer.Add(ghost);
        item.Opacity = 0.35;
        _s = new State { Panel = panel, Item = item, Capture = capture, Ghost = ghost, Dropped = dropped };
        capture.CaptureMouse();
        capture.MouseMove += OnMove;
        capture.MouseLeftButtonUp += OnUp;
        capture.LostMouseCapture += OnLost;
        _timer.Start();
        Update(e.GetPosition(panel));
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e) => Safe(() => { if (_s is { } s) Update(e.GetPosition(s.Panel)); });
    void OnUp(object sender, MouseButtonEventArgs e) => Safe(() => End(drop: true));
    void OnLost(object sender, MouseEventArgs e) => Safe(() => End(drop: false));

    List<FrameworkElement> Others(State s) =>
        s.Panel.Children.OfType<FrameworkElement>().Where(x => x != s.Item && !Equals(x.Tag, GapTag)).ToList();

    void Update(Point p)
    {
        if (_s is not { } s) return;
        s.Ghost.MoveTo(p.Y);
        var others = Others(s);
        var target = others.Count(o => o.TranslatePoint(new Point(0, 0), s.Panel).Y + o.ActualHeight / 2 < p.Y);
        var original = s.Panel.Children.OfType<FrameworkElement>().Where(x => !Equals(x.Tag, GapTag)).ToList().IndexOf(s.Item);
        if (target == original) target = -1;
        if (target != s.Target)
        {
            s.Target = target;
            if (s.Gap is { } old) Close(old);
            s.Gap = null;
            if (target >= 0)
            {
                var gap = DropLine();
                var before = target < others.Count ? others[target] : null;
                s.Panel.Children.Insert(before is null ? s.Panel.Children.Count : s.Panel.Children.IndexOf(before), gap);
                gap.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, DropHeight, Time) { EasingFunction = new QuadraticEase() });
                s.Gap = gap;
            }
        }
        var inView = s.Panel.TranslatePoint(p, _scroll);
        s.ScrollDirection = inView.Y < EdgeZone ? -1 : inView.Y > _scroll.ActualHeight - EdgeZone ? 1 : 0;
    }

    void ScrollTick()
    {
        if (_s is not { ScrollDirection: not 0 } s) return;
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + s.ScrollDirection * ScrollStep);
        Update(Mouse.GetPosition(s.Panel));
    }

    void End(bool drop)
    {
        if (_s is not { } s) return;
        _s = null;
        _timer.Stop();
        s.Capture.MouseMove -= OnMove;
        s.Capture.MouseLeftButtonUp -= OnUp;
        s.Capture.LostMouseCapture -= OnLost;
        if (s.Capture.IsMouseCaptured) s.Capture.ReleaseMouseCapture();
        AdornerLayer.GetAdornerLayer(s.Panel)?.Remove(s.Ghost);
        s.Item.Opacity = 1;
        foreach (var gap in s.Panel.Children.OfType<FrameworkElement>().Where(x => Equals(x.Tag, GapTag)).ToList()) s.Panel.Children.Remove(gap);
        if (!drop || s.Target < 0) return;
        s.Panel.Children.Remove(s.Item);
        var others = s.Panel.Children.OfType<FrameworkElement>().ToList();
        s.Panel.Children.Insert(s.Target < others.Count ? s.Panel.Children.IndexOf(others[s.Target]) : s.Panel.Children.Count, s.Item);
        s.Dropped(s.Target);
    }

    static void Close(Grid gap)
    {
        var shrink = new DoubleAnimation(gap.ActualHeight, 0, Time) { EasingFunction = new QuadraticEase() };
        shrink.Completed += (_, _) => (gap.Parent as Panel)?.Children.Remove(gap);
        gap.BeginAnimation(FrameworkElement.HeightProperty, shrink);
    }

    // «Вставить сюда» between two sparse dashed lines, full width.
    static Grid DropLine()
    {
        var grid = new Grid { Tag = GapTag, Height = 0, ClipToBounds = true, IsHitTestVisible = false };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = Tr.T("Insert here"), FontSize = 12, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "SystemAccentColorSecondaryBrush");
        Grid.SetColumn(text, 1);
        grid.Children.Add(Dash(0));
        grid.Children.Add(text);
        grid.Children.Add(Dash(2));
        return grid;
    }

    static Line Dash(int column)
    {
        var line = new Line { X1 = 0, X2 = 1, Stretch = Stretch.Fill, StrokeThickness = 1.5, StrokeDashArray = [2, 6], VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(Shape.StrokeProperty, "SystemAccentColorSecondaryBrush");
        Grid.SetColumn(line, column);
        return line;
    }

    sealed class Ghost : Adorner
    {
        readonly ImageBrush _brush;
        readonly Size _size;
        readonly double _grab, _x;
        double _y;

        public Ghost(UIElement panel, FrameworkElement item, Point grab) : base(panel)
        {
            _brush = new ImageBrush(BoardDrag.Snapshot(item)) { Opacity = 0.85 };   // taken before the item dims
            _size = new Size(item.ActualWidth, item.ActualHeight);
            _grab = grab.Y;
            _x = item.TranslatePoint(new Point(0, 0), panel).X;
            IsHitTestVisible = false;
        }

        public void MoveTo(double y) { _y = y; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc) =>
            dc.DrawRectangle(_brush, null, new Rect(new Point(_x, _y - _grab), _size));
    }
}
