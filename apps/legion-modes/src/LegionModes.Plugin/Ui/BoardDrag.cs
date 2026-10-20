using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace LegionModes.Plugin.Ui;

// Moving blocks between the columns of a board, as a kanban board does: the dragged block follows the mouse, the place it
// would take opens as a dashed ghost of its size (the blocks below slide apart), the place it leaves closes, and the
// scroll viewer scrolls while the mouse is near its top or bottom edge.
internal sealed class BoardDrag
{
    const double EdgeZone = 48, ScrollStep = 16;                 // 16 px per 30 ms (~530 px/s): twice the profile list's
    const double BlockGap = 10;                                  // a card's bottom margin
    static readonly Duration Time = TimeSpan.FromMilliseconds(140);

    readonly Panel[] _columns;
    readonly ScrollViewer _scroll;
    readonly FrameworkElement _surface;
    readonly Action _dropped;
    readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    State? _s;

    sealed class State
    {
        public required FrameworkElement Block;
        public required FrameworkElement Handle;
        public required Ghost Ghost;
        public required Grid Placeholder;
        public required double Height;
        public double ScrollDirection;
    }

    public BoardDrag(Panel[] columns, ScrollViewer scroll, FrameworkElement surface, Action dropped)
    {
        _columns = columns;
        _scroll = scroll;
        _surface = surface;
        _dropped = dropped;
        _timer.Tick += (_, _) => Safe(ScrollTick);
    }

    static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { Log.Error("board drag", e); }
    }

    public void Begin(FrameworkElement block, FrameworkElement handle, MouseButtonEventArgs e)
    {
        if (_s is not null || block.Parent is not Panel column || AdornerLayer.GetAdornerLayer(_surface) is not { } layer) return;
        var grab = e.GetPosition(block);
        var ghost = new Ghost(_surface, block, grab);
        layer.Add(ghost);
        var index = column.Children.IndexOf(block);
        var placeholder = Placeholder(block.ActualHeight);
        placeholder.Height = block.ActualHeight + BlockGap;          // the block's own place, already open
        column.Children.RemoveAt(index);
        column.Children.Insert(index, placeholder);
        _s = new State { Block = block, Handle = handle, Ghost = ghost, Placeholder = placeholder, Height = block.ActualHeight };
        // The surface takes the mouse: the handle left the tree with its block, and an element out of the tree cannot.
        _surface.MouseMove += OnMove;
        _surface.MouseLeftButtonUp += OnUp;
        _surface.LostMouseCapture += OnLost;
        if (!_surface.CaptureMouse()) { Safe(End); return; }
        _timer.Start();
        Update(e.GetPosition(_surface));
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e) => Safe(() => Update(e.GetPosition(_surface)));
    void OnUp(object sender, MouseButtonEventArgs e) => Safe(End);
    void OnLost(object sender, MouseEventArgs e) => Safe(End);

    void Update(Point p)
    {
        if (_s is not { } s) return;
        s.Ghost.MoveTo(p);

        // The column under the mouse (the nearest by x), then how many of its blocks have their middle above the mouse.
        var column = _columns.OrderBy(c =>
        {
            var left = c.TranslatePoint(new Point(0, 0), _surface).X;
            return p.X < left ? left - p.X : p.X > left + c.ActualWidth ? p.X - left - c.ActualWidth : 0;
        }).First();
        var blocks = column.Children.OfType<FrameworkElement>().Where(IsBlock).ToList();
        var above = blocks.Count(b => b.TranslatePoint(new Point(0, 0), _surface).Y + b.ActualHeight / 2 < p.Y);
        var before = above < blocks.Count ? blocks[above] : null;      // the place: in `column`, right before `before` (null = last)

        var current = s.Placeholder.Parent as Panel;
        var next = current?.Children.OfType<FrameworkElement>().SkipWhile(x => x != s.Placeholder).Skip(1).FirstOrDefault(IsBlock);
        if (current != column || next != before)
        {
            Close(s.Placeholder);
            var open = Placeholder(s.Height);
            column.Children.Insert(before is null ? column.Children.Count : column.Children.IndexOf(before), open);
            open.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, s.Height + BlockGap, Time) { EasingFunction = new QuadraticEase() });
            s.Placeholder = open;
        }

        var inView = _surface.TranslatePoint(p, _scroll);
        s.ScrollDirection = inView.Y < EdgeZone ? -1 : inView.Y > _scroll.ActualHeight - EdgeZone ? 1 : 0;
    }

    void ScrollTick()
    {
        if (_s is not { ScrollDirection: not 0 } s) return;
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + s.ScrollDirection * ScrollStep);
        Update(Mouse.GetPosition(_surface));
    }

    void End()
    {
        if (_s is not { } s) return;
        _s = null;
        _timer.Stop();
        _surface.MouseMove -= OnMove;
        _surface.MouseLeftButtonUp -= OnUp;
        _surface.LostMouseCapture -= OnLost;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        AdornerLayer.GetAdornerLayer(_surface)?.Remove(s.Ghost);
        var column = (Panel)s.Placeholder.Parent;
        var at = column.Children.IndexOf(s.Placeholder);
        column.Children.RemoveAt(at);
        column.Children.Insert(at, s.Block);
        foreach (var c in _columns)
            foreach (var dying in c.Children.OfType<Grid>().Where(IsPlaceholder).ToList()) c.Children.Remove(dying);
        _dropped();
    }

    // The place a block leaves closes, then goes.
    static void Close(Grid placeholder)
    {
        var shrink = new DoubleAnimation(placeholder.ActualHeight, 0, Time) { EasingFunction = new QuadraticEase() };
        shrink.Completed += (_, _) => (placeholder.Parent as Panel)?.Children.Remove(placeholder);
        placeholder.BeginAnimation(FrameworkElement.HeightProperty, shrink);
    }

    const string PlaceholderTag = "board-placeholder";

    static bool IsPlaceholder(FrameworkElement e) => Equals(e.Tag, PlaceholderTag);

    static bool IsBlock(FrameworkElement e) => !IsPlaceholder(e) && e.Visibility == Visibility.Visible;

    // A picture of the element as drawn now (an element taken out of the tree draws nothing through a VisualBrush).
    public static ImageSource Snapshot(FrameworkElement e)
    {
        var dpi = VisualTreeHelper.GetDpi(e);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(e.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(e.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(e)           // its own layout box, not the bounds of what it draws
            {
                Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, e.ActualWidth, e.ActualHeight),
                AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            }, null, new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // A dashed ghost of the block's size (accent stroke, a faint fill), the card gap below it.
    static Grid Placeholder(double height)
    {
        var rect = new Rectangle
        {
            RadiusX = 12, RadiusY = 12, StrokeThickness = 1.5, StrokeDashArray = [4, 4], Height = height,
            VerticalAlignment = VerticalAlignment.Top,
        };
        rect.SetResourceReference(Shape.StrokeProperty, "SystemAccentColorSecondaryBrush");
        rect.SetResourceReference(Shape.FillProperty, "SubtleFillColorSecondaryBrush");
        return new Grid { Tag = PlaceholderTag, Height = 0, ClipToBounds = true, Children = { rect } };
    }

    // The dragged block drawn at the mouse.
    sealed class Ghost : Adorner
    {
        readonly ImageBrush _brush;
        readonly Size _size;
        readonly Point _grab;
        Point _at;

        public Ghost(UIElement surface, FrameworkElement block, Point grab) : base(surface)
        {
            _brush = new ImageBrush(Snapshot(block)) { Opacity = 0.85 };
            _size = new Size(block.ActualWidth, block.ActualHeight);
            _grab = grab;
            IsHitTestVisible = false;
        }

        public void MoveTo(Point p) { _at = p; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc) =>
            dc.DrawRectangle(_brush, null, new Rect(new Point(_at.X - _grab.X, _at.Y - _grab.Y), _size));
    }
}
