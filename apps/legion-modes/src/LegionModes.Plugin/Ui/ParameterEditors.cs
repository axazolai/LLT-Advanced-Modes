using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LegionModes.Core;
using LegionModes.Core.Model;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;

namespace LegionModes.Plugin.Ui;

// The controls of one parameter as "label: control" rows. `set(change, rebuild)` applies `change` to the parameter's
// current own value, so two edits in a row never undo each other; rebuild = the card's controls change shape (a choice
// that shows or hides rows); number fields set without rebuilding so the caret stays.
internal static class ParameterEditors
{
    public delegate void Setter(Func<object, object> change, bool rebuild);

    public static FrameworkElement Build(ParameterId id, object value, Conditions conditions, int maxBrightness, Setter set) => id switch
    {
        ParameterId.Power => Power((PowerSetting)value, set),
        ParameterId.Gpu => Rows(Row(Tr.T("Mode"), Combo(Enum.GetValues<GpuMode>().Select(g => (g, ValueText.Gpu(g))), (GpuMode)value, v => set(_ => v, false)))),
        ParameterId.Panel => Panel((PanelRate)value, set),
        ParameterId.External => External((ExternalMonitor)value, set),
        ParameterId.Charging => Rows(Row(Tr.T("Mode"), Combo(Enum.GetValues<ChargingMode>().Select(c => (c, ValueText.Charging(c))), (ChargingMode)value, v => set(_ => v, false)))),
        ParameterId.Usb => Rows(Row(Tr.T("USB power"), Combo(Enum.GetValues<UsbPower>().Select(u => (u, ValueText.Usb(u))), (UsbPower)value, v => set(_ => v, false)))),
        ParameterId.Processor => Processor((ProcessorScheme)value, set),
        ParameterId.Timers => Timers((IdleTimers)value, conditions, maxBrightness, set),
        _ => Lighting((Lighting)value, set),
    };

    static FrameworkElement Power(PowerSetting p, Setter set)
    {
        var options = Enum.GetValues<FirmwareMode>().Select(f => ((FirmwareMode?)f, ValueText.Firmware(f))).Append(((FirmwareMode?)null, Tr.T("Own GodMode preset")));
        return Rows(Row(Tr.T("Mode"), Combo(options, p.Firmware, v => set(_ => new PowerSetting(v), true))));
    }

    static FrameworkElement Panel(PanelRate r, Setter set)
    {
        var kinds = new[] { (PanelRateKind.DoNotTouch, Tr.T("Do not touch")), (PanelRateKind.Drr, Tr.T("DRR (120 Hz, down to 60)")), (PanelRateKind.Fixed, Tr.T("Fixed|2")) };
        var rows = new List<UIElement>
        {
            Row(Tr.T("Refresh rate"), Combo(kinds, r.Kind, k => set(o => k switch
            {
                PanelRateKind.Fixed => new PanelRate(k, FirmwareRanges.PanelHz.Contains(((PanelRate)o).Hz) ? ((PanelRate)o).Hz : 120),
                PanelRateKind.Drr => new PanelRate(k, 120),
                _ => new PanelRate(k),
            }, true))),
        };
        if (r.Kind == PanelRateKind.Fixed)
            rows.Add(Row(Tr.T("Hz"), Combo(FirmwareRanges.PanelHz.Select(h => (h, h.ToString(CultureInfo.InvariantCulture))), r.Hz,
                h => set(o => (PanelRate)o with { Hz = h }, false))));
        return Rows(rows.ToArray());
    }

    static FrameworkElement External(ExternalMonitor m, Setter set)
    {
        var rates = new[] { (ExternalRateKind.DoNotTouch, Tr.T("Do not touch")), (ExternalRateKind.Maximum, Tr.T("Maximum")), (ExternalRateKind.Fixed, Tr.T("Fixed|2")) };
        var rows = new List<UIElement>
        {
            Row(Tr.T("Projection"), Combo(Enum.GetValues<Projection>().Select(p => (p, ValueText.Projection(p))), m.Projection,
                p => set(o => (ExternalMonitor)o with { Projection = p }, false))),
            Row(Tr.T("Refresh rate"), Combo(rates, m.Rate.Kind, k => set(o =>
            {
                var cur = (ExternalMonitor)o;
                return cur with { Rate = new ExternalRate(k, k == ExternalRateKind.Fixed ? Math.Max(cur.Rate.Hz, 60) : 0) };
            }, true))),
        };
        if (m.Rate.Kind == ExternalRateKind.Fixed)
            rows.Add(Row(Tr.T("Hz"), Number(m.Rate.Hz, 24, 500, h => set(o => (ExternalMonitor)o with { Rate = ((ExternalMonitor)o).Rate with { Hz = h } }, false))));
        return Rows(rows.ToArray());
    }

    static FrameworkElement Processor(ProcessorScheme s, Setter set)
    {
        var epp = new StackPanel { Orientation = Orientation.Horizontal };
        var eppOn = new CheckBox { Content = Tr.T("set"), IsChecked = s.Epp is not null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        eppOn.Click += (_, _) => set(o => (ProcessorScheme)o with { Epp = eppOn.IsChecked == true ? 50 : null }, true);
        epp.Children.Add(eppOn);
        if (s.Epp is { } e) epp.Children.Add(Number(e, 0, 100, v => set(o => (ProcessorScheme)o with { Epp = v }, false)));
        return Rows(
            Row(Tr.T("Turbo boost"), Combo(Enum.GetValues<BoostMode>().Select(b => (b, Capital(ValueText.Boost(b)))), s.Boost,
                b => set(o => (ProcessorScheme)o with { Boost = b }, false))),
            Row(Tr.T("EPP (0 — speed, 100 — saving)"), epp),
            Row(Tr.T("Windows power mode"), Combo(Enum.GetValues<WindowsPowerMode>().Select(w => (w, Capital(ValueText.WindowsMode(w)))), s.PowerMode,
                w => set(o => (ProcessorScheme)o with { PowerMode = w }, false))));
    }

    enum DimKind { None, Static, Dim }

    static FrameworkElement Timers(IdleTimers t, Conditions conditions, int maxBrightness, Setter set)
    {
        var kind = t.Dim switch { null => DimKind.None, { Minutes: 0 } => DimKind.Static, _ => DimKind.Dim };
        var kinds = new[] { (DimKind.None, Tr.T("Do not touch")), (DimKind.Static, Tr.T("Fixed")), (DimKind.Dim, Tr.T("Dim when idle")) };
        var rows = new List<UIElement>
        {
            Row(Tr.T("Screen brightness"), Combo(kinds, kind, k => set(o =>
            {
                var cur = (IdleTimers)o;
                return cur with
                {
                    Dim = k switch
                    {
                        DimKind.None => null,
                        DimKind.Static => new DimStage(0, cur.Dim?.Brightness ?? 50),
                        _ => new DimStage(Math.Max(cur.Dim?.Minutes ?? 0, 1), cur.Dim?.Brightness ?? 30),
                    },
                };
            }, true))),
        };
        if (t.Dim is { } d)
        {
            if (d.Minutes > 0)
                rows.Add(Row(Tr.T("Dim after, min"), Number(d.Minutes, 1, 1440, v => set(o => (IdleTimers)o with { Dim = ((IdleTimers)o).Dim! with { Minutes = v } }, false))));
            rows.Add(Row(d.Minutes > 0 ? Tr.T("Brightness after dimming") : Tr.T("Brightness"), Number(d.Brightness, 0, maxBrightness,
                v => set(o => (IdleTimers)o with { Dim = ((IdleTimers)o).Dim! with { Brightness = v } }, false))));
        }
        rows.Add(Row(Tr.T("Turn the screen off after, min (0 — never)"), Number(t.DisplayOffMin, 0, 1440, v => set(o => (IdleTimers)o with { DisplayOffMin = v }, false))));
        rows.Add(Row(Tr.T("Sleep after, min (0 — never)"), Number(t.SleepMin, 0, 1440, v => set(o => (IdleTimers)o with { SleepMin = v }, false))));
        rows.Add(Row(Tr.T("Hibernate after sleep, min (0 — never)"), Number(t.HibernateMin, 0, 1440, v => set(o => (IdleTimers)o with { HibernateMin = v }, false))));
        if (conditions.Lid == LidState.Closed)
        {
            var forced = new StackPanel { Orientation = Orientation.Horizontal };
            var on = new CheckBox { Content = Tr.T("after"), IsChecked = t.ForcedSleepMin is not null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            on.Click += (_, _) => set(o => (IdleTimers)o with { ForcedSleepMin = on.IsChecked == true ? 1 : null }, true);
            forced.Children.Add(on);
            if (t.ForcedSleepMin is { } f) forced.Children.Add(Number(f, 1, 1440, v => set(o => (IdleTimers)o with { ForcedSleepMin = v }, false)));
            rows.Add(Row(Tr.T("Sleep with the lid closed and no monitor, min"), forced));
        }
        return Rows(rows.ToArray());
    }

    static FrameworkElement Lighting(Lighting l, Setter set) => Rows(
        Row(Tr.T("Spectrum profile"), Combo(Enumerable.Range(1, 6).Select(i => (i, i.ToString(CultureInfo.InvariantCulture))), l.Profile,
            p => set(o => (Lighting)o with { Profile = p }, false))),
        Row(Tr.T("Brightness (0 — off)"), Combo(Enumerable.Range(0, 10).Select(i => (i, i.ToString(CultureInfo.InvariantCulture))), l.Brightness,
            b => set(o => (Lighting)o with { Brightness = b }, false))));

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // Label on the left (12 Regular, Secondary), control on the right, as the reference's "label: value" rows.
    static UIElement Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    static FrameworkElement Rows(params UIElement[] rows)
    {
        var panel = new StackPanel();
        foreach (var r in rows) panel.Children.Add(r);
        return panel;
    }

    static ComboBox Combo<T>(IEnumerable<(T Value, string Label)> items, T selected, Action<T> changed)
    {
        var box = new ComboBox { MinWidth = 160 };
        foreach (var (value, label) in items)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(value, selected)) box.SelectedItem = item;
        }
        var current = selected;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: var tag } || EqualityComparer<T>.Default.Equals((T)tag!, current)) return;
            current = (T)tag!;
            changed(current);
        };
        return box;
    }

    // A whole-number field; an out-of-range or unreadable entry is not passed on.
    static FrameworkElement Number(int value, int min, int max, Action<int> changed)
    {
        var box = new Wpf.Ui.Controls.NumberBox
        {
            Value = value, Minimum = min, Maximum = max, MaxDecimalPlaces = 0, Width = 120,
        };
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
}
