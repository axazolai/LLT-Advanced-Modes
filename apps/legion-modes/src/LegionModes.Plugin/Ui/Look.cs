using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Common;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;

namespace LegionModes.Plugin.Ui;

// Building blocks in the look of LLT's DeviceInformationWindow (spec section 5, "Visual reference"); the styles are in
// SharedResources, merged into every window that uses these.
internal static class Look
{
    // A window body: LLT's title bar, a title row (16 icon + 13 SemiBold title + extra elements), content, footer.
    public static Grid Frame(SymbolRegular icon, string iconBrush, TextBlock title, UIElement content, UIElement? footer, params UIElement[] titleExtras)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new Wpf.Ui.Controls.TitleBar { CanMaximize = true, ShowMaximize = true, ShowMinimize = false, UseSnapLayout = false });
        var i = new SymbolIcon { Symbol = icon, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        i.SetResourceReference(Control.ForegroundProperty, iconBrush);
        title.FontSize = 13;
        title.FontWeight = FontWeights.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.IsHitTestVisible = false;
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 24, Margin = new Thickness(12, 8, 12, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        row.Children.Add(i);
        row.Children.Add(title);
        foreach (var e in titleExtras) row.Children.Add(e);
        grid.Children.Add(row);

        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        if (footer is not null)
        {
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);
        }
        return grid;
    }

    // A card: palette icon 18 + 15 SemiBold title (row margin 0,0,0,16), then the body.
    public static Border Card(SymbolRegular icon, string brush, string title, UIElement body, UIElement? headerExtra = null)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var i = new SymbolIcon { Symbol = icon, FontSize = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        i.SetResourceReference(Control.ForegroundProperty, brush);
        var t = Text(title, "CardTitle");
        Grid.SetColumn(t, 1);
        header.Children.Add(i);
        header.Children.Add(t);
        if (headerExtra is not null)
        {
            Grid.SetColumn(headerExtra, 2);
            header.Children.Add(headerExtra);
        }
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(body);
        var border = new Border { Child = panel };
        border.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        return border;
    }

    public static TextBlock Text(string text, string style, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (margin is { } m) t.Margin = m;
        return t;
    }

    // "Label: value" row: 16 icon (Secondary), 12 Regular label, the control or value on the right.
    public static UIElement Row(SymbolRegular icon, string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var i = new SymbolIcon { Symbol = icon };
        i.SetResourceReference(FrameworkElement.StyleProperty, "RowIcon");
        var l = Text(label, "RowLabel");
        Grid.SetColumn(l, 1);
        Grid.SetColumn(control, 2);
        grid.Children.Add(i);
        grid.Children.Add(l);
        grid.Children.Add(control);
        return grid;
    }
}
