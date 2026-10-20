using LegionModes.Core;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using LegionModes.Plugin.Ui.ViewModels;

namespace LegionModes.Plugin.Shell;

// The plugin's card at the top of LLT's dashboard. LLT creates the page anew on every navigation, so the card is put in on
// each navigation to it: found by the page type, placed at index 0 of the page's `_panel` unless a tagged child is there.
internal sealed class DashboardCard : IDisposable
{
    public const string Tag = "legion-modes-card";
    const string PageType = "LenovoLegionToolkit.WPF.Pages.DashboardPage";

    readonly Runtime _runtime;
    readonly Frame _frame;
    bool _disposed;

    // `report` gets the outcome of every dashboard visit: an error text, or null once the card is in place.
    readonly Action<string?> _report;

    public DashboardCard(Runtime runtime, Frame frame, Action<string?> report)
    {
        _runtime = runtime;
        _frame = frame;
        _report = report;
        _frame.Navigated += OnNavigated;
        Process(_frame.Content);   // the dashboard is the first page: it may already be shown
    }

    public void Dispose()
    {
        _disposed = true;
        try { _frame.Navigated -= OnNavigated; } catch { }
    }

    void OnNavigated(object sender, NavigationEventArgs e)
    {
        try { Process(e.Content); }
        catch (Exception ex) { Log.Error("dashboard card", ex); }
    }

    void Process(object? page)
    {
        try
        {
            if (_disposed || page is null || page.GetType().FullName != PageType) return;
            var panel = page.GetType().GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(page) as StackPanel;
            if (panel is null)
            {
                _report("dashboard: DashboardPage._panel not found");
                return;
            }
            if (!panel.Children.OfType<FrameworkElement>().Any(c => Tag.Equals(c.Tag))) panel.Children.Insert(0, Build());
            _report(null);
        }
        catch (Exception e)
        {
            Log.Error("dashboard card insert", e);
            _report("dashboard: " + e.Message);
        }
    }

    FrameworkElement Build()
    {
        var card = Build(_runtime);
        card.Tag = Tag;
        return card;
    }

    // The card in the look of the plugin's Status tab: profile in force (green, orange while paused or without control), its
    // reason and the last apply in one row (wrapping when narrow); «Подробнее» opens the plugin's page. Live while it is loaded.
    internal static FrameworkElement Build(Ui.IModesHost host)
    {
        var vm = new StatusViewModel(host);
        var body = new WrapPanel();
        var name = Ui.Look.Text("", "MainValue", new Thickness(0, 0, 20, 0));
        var reason = Ui.Look.Text("", "SubLine", new Thickness(0, 0, 24, 0));
        name.VerticalAlignment = VerticalAlignment.Center;
        reason.VerticalAlignment = VerticalAlignment.Center;
        var apply = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(name);
        body.Children.Add(reason);
        body.Children.Add(apply);
        var more = new Wpf.Ui.Controls.Button { Content = Tr.T("Подробнее"), Icon = Wpf.Ui.Common.SymbolRegular.ArrowRight24 };
        more.Click += (_, _) =>
        {
            try { TrayBlock.OpenPage(); }
            catch (Exception e) { Log.Error("dashboard card open page", e); }
        };
        var card = Ui.Look.Card(Wpf.Ui.Common.SymbolRegular.Layer24, "PaletteLightBlueBrush", Tr.T("Расширенные режимы"), body, more);
        card.Resources.MergedDictionaries.Add(Ui.SharedResources.Instance);
        card.Margin = new Thickness(0, 16, 16, 8);

        void Show()
        {
            try
            {
                vm.Refresh();
                name.Text = vm.InForceName;
                reason.Text = vm.InForceReason;
                reason.SetResourceReference(TextBlock.ForegroundProperty, vm.InForceTone == Tone.Good ? "PaletteGreenBrush" : "PaletteOrangeBrush");
                var line = Ui.StatusTab.Line(new StatusLine(Tr.T("Последнее применение"), vm.ApplyLine));
                if (line is FrameworkElement fe) fe.Margin = new Thickness(0);   // the line's own bottom margin would lift it off the row's centre
                apply.Content = line;
            }
            catch (Exception e) { Log.Error("dashboard card refresh", e); }
        }

        Action<LegionModes.Core.Engine.Decision> onDecision = decision =>
        {
            try { _ = card.Dispatcher.BeginInvoke(Show); }
            catch (Exception e) { Log.Error("dashboard card decision", e); }
        };
        System.ComponentModel.PropertyChangedEventHandler onChanged = (_, _) => Show();
        var subscribed = false;
        card.Loaded += (_, _) =>
        {
            if (!subscribed)
            {
                subscribed = true;
                host.Decided += onDecision;
                host.Status.PropertyChanged += onChanged;
                host.Devices.PropertyChanged += onChanged;
            }
            Show();
        };
        card.Unloaded += (_, _) =>
        {
            if (!subscribed) return;
            subscribed = false;
            host.Decided -= onDecision;
            host.Status.PropertyChanged -= onChanged;
            host.Devices.PropertyChanged -= onChanged;
        };
        return card;
    }
}
