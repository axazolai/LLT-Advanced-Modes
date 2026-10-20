using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LegionModes.Core;
using LegionModes.Core.Programs;
using LegionModes.Plugin.Ui.ViewModels;
using Wpf.Ui.Common;
using static LegionModes.Plugin.Ui.Look;
using Binding = System.Windows.Data.Binding;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using UserControl = System.Windows.Controls.UserControl;
using WpfUiButton = Wpf.Ui.Controls.Button;
using WpfUiTextBox = Wpf.Ui.Controls.TextBox;
using Keyboard = System.Windows.Input.Keyboard;

namespace LegionModes.Plugin.Ui;

// Программы: the edited list on the left, the running processes on the right; each card scrolls on its own.
public partial class ProgramsTab : UserControl
{
    ProgramsViewModel? _vm;
    readonly StackPanel _items = new(), _processes = new();
    readonly WpfUiButton _save = new() { Appearance = ControlAppearance.Primary, Icon = SymbolRegular.Save24, Margin = new Thickness(8, 0, 0, 0) };
    readonly WpfUiButton _revert = new() { Margin = new Thickness(8, 0, 0, 0) };
    readonly WpfUiTextBox _search = new() { Icon = SymbolRegular.Search24, Margin = new Thickness(0, 0, 0, 12) };

    // The process list redraws once typing pauses (the search box, a pattern field).
    readonly System.Windows.Threading.DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    bool _itemsStale;

    public ProgramsTab()
    {
        Resources.MergedDictionaries.Add(SharedResources.Instance);   // before InitializeComponent: StaticResource is resolved while loading
        InitializeComponent();
        _save.Content = Tr.T("Сохранить");
        _revert.Content = Tr.T("Отменить");
        _search.PlaceholderText = Tr.T("Поиск: имя, описание, путь");
        _processTimer.Tick += (_, _) => { _processTimer.Stop(); Safe("processes", RenderProcesses); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Safe("programs search", () => { if (_vm is { } vm) vm.Search = _search.Text; }); };
        // Icons that arrived while a field of the list was being typed in are shown once the field is left.
        _items.IsKeyboardFocusWithinChanged += (_, _) => { if (!_items.IsKeyboardFocusWithin && _itemsStale) Safe("programs list", RenderItems); };
    }

    static void Restart(System.Windows.Threading.DispatcherTimer t)
    {
        t.Stop();
        t.Start();
    }

    public void Bind(ProgramsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        _save.Click += (_, _) => _ = vm.SaveAsync();
        _revert.Click += (_, _) => Safe("programs revert", vm.Revert);
        _search.TextChanged += (_, _) => Restart(_searchTimer);
        vm.ItemsChanged += () => Safe("programs list", RenderItems);
        vm.IconsLoaded += () => Safe("program icons", () =>
        {
            if (_items.IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.Controls.TextBox) _itemsStale = true;
            else RenderItems();
        });
        vm.DirtyChanged += () => Safe("programs buttons", () => _save.IsEnabled = _revert.IsEnabled = vm.Dirty);
        vm.ProcessesChanged += () => Restart(_processTimer);
        _listHost.Content = ListCard(vm);
        _processHost.Content = ProcessCard(vm);
        RenderItems();
        RenderProcesses();
    }

    static void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Error(what, e); }
    }

    // A card that fills its column: header, an optional fixed part, then the scrolling list.
    static Border FillCard(SymbolRegular icon, string brush, string title, UIElement headerExtra, UIElement? top, UIElement list)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var i = new SymbolIcon { Symbol = icon, FontSize = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        i.SetResourceReference(ForegroundProperty, brush);
        var t = Text(title, "CardTitle");
        Grid.SetColumn(t, 1);
        Grid.SetColumn(headerExtra, 2);
        header.Children.Add(i);
        header.Children.Add(t);
        header.Children.Add(headerExtra);
        grid.Children.Add(header);
        if (top is not null)
        {
            Grid.SetRow(top, 1);
            grid.Children.Add(top);
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);
        var border = new Border { Child = grid, Margin = new Thickness(0) };
        border.SetResourceReference(StyleProperty, "Card");
        return border;
    }

    UIElement ListCard(ProgramsViewModel vm)
    {
        var add = new WpfUiButton { Content = Tr.T("Добавить"), Icon = SymbolRegular.Add24 };
        add.Click += (_, _) => Safe("program add", () => vm.Add(new ProgramItem { Expanded = true }));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(add);
        buttons.Children.Add(_revert);
        buttons.Children.Add(_save);

        var top = new StackPanel();
        top.Children.Add(Text(Tr.T("Шаблон: имя exe (game.exe), путь (C:\\Games\\**\\game.exe) или папка с \\ на конце; * — внутри одного уровня, ** — любая глубина. " +
                              "Из нескольких подходящих записей действует самый тяжёлый класс."), "SubLine", new Thickness(0, 0, 0, 8)));
        var message = new TextBlock();
        message.SetResourceReference(StyleProperty, "MessageText");
        message.SetBinding(TextBlock.TextProperty, new Binding(nameof(TabViewModel.Message)));
        message.SetBinding(VisibilityProperty, new Binding(nameof(TabViewModel.HasMessage)) { Converter = new BooleanToVisibilityConverter() });
        message.Margin = new Thickness(0, 0, 0, 8);
        top.Children.Add(message);
        return FillCard(SymbolRegular.TextBulletListLtr24, "PaletteLightBlueBrush", Tr.T("Список программ"), buttons, top, _items);
    }

    UIElement ProcessCard(ProgramsViewModel vm)
    {
        var refresh = new WpfUiButton { Icon = SymbolRegular.ArrowClockwise24, ToolTip = Tr.T("Обновить список процессов") };
        refresh.Click += (_, _) => _ = vm.LoadProcessesAsync();
        return FillCard(SymbolRegular.AppGeneric24, "PaletteGreenBrush", Tr.T("Запущенные процессы"), refresh, _search, _processes);
    }

    void RenderItems()
    {
        if (_vm is not { } vm) return;
        _itemsStale = false;
        _save.IsEnabled = _revert.IsEnabled = vm.Dirty;
        _items.Children.Clear();
        if (vm.Items.Count == 0) _items.Children.Add(Text(Tr.T("Список пуст: процессы не влияют на нагрузку."), "SubLine"));
        foreach (var item in vm.Items) _items.Children.Add(ItemRow(vm, item));
    }

    static SymbolRegular ClassIcon(ProgramClass c) => c == ProgramClass.Ignore ? SymbolRegular.EyeOff24 : Texts.WorkloadIcon((Core.Model.Workload)(int)c);

    // Icon, pattern and its details; class, settings and delete at the right; the settings open under the row.
    UIElement ItemRow(ProgramsViewModel vm, ProgramItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // A concrete exe path shows that exe's icon, anything else the class icon.
        var pattern = item.Pattern.Trim();
        UIElement icon = item.ExePath is { } exe && ProcessIcons.TryCached(exe, out var image) && image is not null
            ? new Image { Source = image, Width = 20, Height = 20, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top }
            : new SymbolIcon { Symbol = ClassIcon(item.Class), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
        if (icon is SymbolIcon symbol) symbol.SetResourceReference(StyleProperty, "RowIcon");
        row.Children.Add(icon);

        // The exe or the last folder as the title, the whole pattern under it.
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = pattern.TrimEnd('\\', '*').Split('\\').LastOrDefault(x => x.Length > 0) ?? "";
        var name = Text(pattern.Length == 0 ? Tr.T("новая запись") : title.Length > 0 ? title : pattern, "ItemName");
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        var details = new List<string>();
        if (pattern != title && pattern.Length > 0) details.Add(pattern);
        if (item.CommandLine.Trim().Length > 0) details.Add(Tr.T("командная строка: ") + item.CommandLine.Trim());
        if (item.DenyWakeRequests) details.Add(Tr.T("не даёт держать экран и сон"));
        if (details.Count > 0) text.Children.Add(Text(string.Join(" · ", details), "SubLine", new Thickness(0, 2, 0, 0)));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var combo = new ComboBox { MinWidth = 150 };
        foreach (var c in ProgramsViewModel.Classes) combo.Items.Add(new ComboBoxItem { Content = ProgramsViewModel.ClassName(c), Tag = c });
        combo.SelectedIndex = ProgramsViewModel.Classes.ToList().IndexOf(item.Class);
        combo.SelectionChanged += (_, _) => Safe("program class", () =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: ProgramClass c } && c != item.Class) { item.Class = c; vm.Edited(); }
        });
        right.Children.Add(combo);
        var gear = new WpfUiButton { Icon = SymbolRegular.Settings24, ToolTip = Tr.T("Шаблон, командная строка, сон"), Margin = new Thickness(8, 0, 0, 0) };
        gear.Click += (_, _) => Safe("program expand", () => { item.Expanded = !item.Expanded; RenderItems(); });
        right.Children.Add(gear);
        var delete = new WpfUiButton { Icon = SymbolRegular.Delete24, ToolTip = Tr.T("Удалить"), Margin = new Thickness(8, 0, 0, 0) };
        delete.Click += (_, _) => Safe("program delete", () => vm.Remove(item));
        right.Children.Add(delete);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);

        if (item.Expanded)
        {
            var editor = Editor(vm, item);
            Grid.SetRow(editor, 1);
            Grid.SetColumn(editor, 1);
            Grid.SetColumnSpan(editor, 2);
            row.Children.Add(editor);
        }
        return row;
    }

    // The fields commit while typing without redrawing the list (the field under the cursor stays); the row's title follows
    // on the next redraw.
    static UIElement Editor(ProgramsViewModel vm, ProgramItem item)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        var pattern = new WpfUiTextBox { Text = item.Pattern, PlaceholderText = Tr.T("Шаблон: game.exe или C:\\Games\\**\\game.exe") };
        pattern.TextChanged += (_, _) => Safe("program pattern", () => { if (pattern.Text != item.Pattern) { item.Pattern = pattern.Text; vm.Touched(); } });
        panel.Children.Add(Field(Tr.T("Шаблон"), pattern));
        var commandLine = new WpfUiTextBox { Text = item.CommandLine, PlaceholderText = Tr.T("любая; * — любые символы, например *serve*") };
        commandLine.TextChanged += (_, _) => Safe("program command line", () => { if (commandLine.Text != item.CommandLine) { item.CommandLine = commandLine.Text; vm.Touched(); } });
        panel.Children.Add(Field(Tr.T("Командная строка"), commandLine));
        var deny = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = item.DenyWakeRequests, VerticalAlignment = VerticalAlignment.Center };
        deny.Click += (_, _) => Safe("program deny wake", () => { item.DenyWakeRequests = deny.IsChecked == true; vm.Edited(); });
        panel.Children.Add(Row(SymbolRegular.WeatherMoon24, Tr.T("Не давать держать экран и сон (шаблон должен кончаться именем exe без *)"), deny));
        return panel;
    }

    static UIElement Field(string label, UIElement box)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(Text(label, "RowLabel", new Thickness(0, 0, 0, 4)));
        panel.Children.Add(box);
        return panel;
    }

    void RenderProcesses()
    {
        if (_vm is not { } vm) return;
        _processes.Children.Clear();
        var windowed = vm.Processes.Where(p => p.Windowed).ToList();
        var rest = vm.Processes.Where(p => !p.Windowed).ToList();
        if (vm.Processes.Count == 0) _processes.Children.Add(Text(vm.Search.Length > 0 ? Tr.T("ничего не найдено") : Tr.T("читается…"), "SubLine"));
        if (windowed.Count > 0) _processes.Children.Add(Group(Tr.F("С окнами ({0})", windowed.Count)));
        foreach (var p in windowed) _processes.Children.Add(ProcessRow(vm, p));
        if (rest.Count > 0) _processes.Children.Add(Group(Tr.F("Фоновые и службы ({0})", rest.Count)));
        foreach (var p in rest) _processes.Children.Add(ProcessRow(vm, p));
    }

    static UIElement Group(string title) => Text(title, "RowLabel", new Thickness(0, 4, 0, 8));

    // Exe icon, display name, exe and path; the class it gets from the list (or «нет в списке»), «В список» with the classes.
    static UIElement ProcessRow(ProgramsViewModel vm, ProcessItem p)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UIElement icon = ProcessIcons.Of(p.Path) is { } image
            ? new Image { Source = image, Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }
            : new SymbolIcon { Symbol = SymbolRegular.AppGeneric24, FontSize = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        if (icon is SymbolIcon s) s.SetResourceReference(ForegroundProperty, "TextFillColorTertiaryBrush");
        row.Children.Add(icon);

        var text = new StackPanel();
        var name = Text(p.Display, "ItemName");
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        var sub = Text(p.Path is null ? Tr.F("{0} · путь недоступен", p.Exe) : p.Display == p.Exe ? p.Path : p.Exe + " · " + p.Path, "SubLine", new Thickness(0, 2, 0, 0));
        sub.TextWrapping = TextWrapping.NoWrap;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.ToolTip = p.Path;
        text.Children.Add(sub);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var mark = Text(p.Class is { } c ? ProgramsViewModel.ClassName(c) : Tr.T("нет в списке"), "RowValue", new Thickness(0, 0, 8, 0));
        if (p.Class is null) mark.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        right.Children.Add(mark);
        var add = new WpfUiButton { Icon = SymbolRegular.Add24, ToolTip = Tr.T("В список") };
        add.Click += (_, _) => Safe("process menu", () =>
        {
            var menu = new ContextMenu { PlacementTarget = add };       // built on the click: a list has hundreds of rows
            foreach (var cls in ProgramsViewModel.Classes)
            {
                var item = new Wpf.Ui.Controls.MenuItem { Header = ProgramsViewModel.ClassName(cls), SymbolIcon = ClassIcon(cls) };
                item.Click += (_, _) => Safe("process add", () => vm.AddProcess(p, cls));
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        });
        right.Children.Add(add);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return row;
    }
}
