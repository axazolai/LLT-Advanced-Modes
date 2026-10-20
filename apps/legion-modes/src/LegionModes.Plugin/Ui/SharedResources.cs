using System.Windows;
using System.Windows.Controls;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using System.Windows.Data;

namespace LegionModes.Plugin.Ui;

// Styles and the converter the tabs share. Built in code so a tab can use them while its own markup loads (before it has a
// parent); the brushes are LLT's theme keys, looked up when the style is applied.
internal static class SharedResources
{
    public static ResourceDictionary Instance { get; } = Build();

    static ResourceDictionary Build()
    {
        var d = new ResourceDictionary { ["B2V"] = new BooleanToVisibilityConverter() };

        var secondary = new Style(typeof(TextBlock));
        secondary.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush")));
        secondary.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        d["Secondary"] = secondary;

        var error = new Style(typeof(TextBlock));
        error.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemFillColorCriticalBrush")));
        error.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        d["ErrorText"] = error;

        var message = new Style(typeof(TextBlock));
        message.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        message.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 8, 0, 0)));
        var onError = new DataTrigger { Binding = new System.Windows.Data.Binding("IsError"), Value = true };
        onError.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemFillColorCriticalBrush")));
        message.Triggers.Add(onError);
        d["MessageText"] = message;

        var heading = new Style(typeof(TextBlock));
        heading.Setters.Add(new Setter(TextBlock.FontSizeProperty, 16.0));
        heading.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        heading.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 8, 0, 8)));
        d["Heading"] = heading;

        AddLook(d);
        return d;
    }

    // The type ladder of LLT's DeviceInformationWindow.xaml (spec section 5, "Visual reference"); values copied from it.
    static void AddLook(ResourceDictionary d)
    {
        var card = new Style(typeof(Border));
        card.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 10)));
        card.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(16)));
        card.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("CardBackgroundFillColorDefaultBrush")));
        card.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("CardStrokeColorDefaultSolidBrush")));
        card.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        card.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(12)));
        d["Card"] = card;

        var header = new Style(typeof(StackPanel));
        header.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 16)));
        header.Setters.Add(new Setter(StackPanel.OrientationProperty, Orientation.Horizontal));
        d["CardHeader"] = header;

        d["CardIcon"] = Icon(18, null);
        d["RowIcon"] = Icon(16, "TextFillColorSecondaryBrush");
        d["CardTitle"] = Text(15, FontWeights.SemiBold, "TextFillColorPrimaryBrush");
        d["MainValue"] = Text(15, FontWeights.Bold, "TextFillColorPrimaryBrush");
        d["ItemName"] = Text(14, FontWeights.Bold, "TextFillColorPrimaryBrush");
        d["SubLine"] = Text(12, FontWeights.Normal, "TextFillColorSecondaryBrush");
        d["RowLabel"] = Text(12, FontWeights.Normal, "TextFillColorSecondaryBrush");
        d["RowValue"] = Text(12, FontWeights.Medium, "TextFillColorSecondaryBrush");
    }

    // Based on the implicit Wpf.Ui style in LLT's app resources: it carries the icon's template.
    static Style Icon(double size, string? brush)
    {
        var type = typeof(Wpf.Ui.Controls.SymbolIcon);
        var s = new Style(type, System.Windows.Application.Current?.TryFindResource(type) as Style);
        s.Setters.Add(new Setter(Control.FontSizeProperty, size));
        s.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)));
        s.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        if (brush is not null) s.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(brush)));
        return s;
    }

    static Style Text(double size, FontWeight weight, string brush)
    {
        var s = new Style(typeof(TextBlock));
        s.Setters.Add(new Setter(TextBlock.FontSizeProperty, size));
        s.Setters.Add(new Setter(TextBlock.FontWeightProperty, weight));
        s.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(brush)));
        s.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        s.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        return s;
    }
}
