using Application = System.Windows.Application;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Control = System.Windows.Controls.Control;
using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Ipc;
using LegionModes.Core.Model;
using LegionModes.Core.Status;
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace LegionModes.Plugin.Shell;

// The plugin's block at the top of LLT's tray menu. Every item carries Tag = "legion-modes"; a rebuild removes the tagged
// items and inserts a fresh block at index 0. All members run on the UI thread. Every action goes through Runtime.Quick.
internal sealed class TrayBlock : IDisposable
{
    public const string Tag = "legion-modes";

    readonly Ui.IModesHost _runtime;
    readonly ContextMenu _menu;
    MenuItem? _updates;
    string? _updateVersion;                 // a compatible release newer than the installed LLT, else null
    int _refreshing;
    bool _disposed;

    public TrayBlock(Ui.IModesHost runtime, ContextMenu menu)
    {
        _runtime = runtime;
        _menu = menu;
        _menu.Opened += OnOpened;
        _runtime.Decided += OnDecisionChanged;
        _runtime.Devices.PropertyChanged += OnDevicesChanged;
        _runtime.Status.PropertyChanged += OnStatusChanged;
    }

    void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusModel.HasControl)) Guard("tray rebuild", Insert);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _menu.Opened -= OnOpened; } catch { }
        try { _runtime.Decided -= OnDecisionChanged; } catch { }
        try { _runtime.Devices.PropertyChanged -= OnDevicesChanged; } catch { }
        try { _runtime.Status.PropertyChanged -= OnStatusChanged; } catch { }
        try { Remove(); } catch (Exception e) { Log.Error("remove tray block", e); }
    }

    void OnOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            Insert(force: true);
            _ = RefreshUpdateFlagAsync();
            _ = _runtime.Devices.RefreshAsync();
        }
        catch (Exception ex) { Log.Error("tray menu opened", ex); }
    }

    void OnDecisionChanged(Decision d)
    {
        try { _ = _menu.Dispatcher.BeginInvoke(() => Guard("tray rebuild", Insert)); }
        catch (Exception e) { Log.Error("tray decision changed", e); }
    }

    void OnDevicesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Guard("tray rebuild", Insert);

    static void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Error(what, e); }
    }

    void Remove()
    {
        foreach (var item in _menu.Items.OfType<Control>().Where(i => Tag.Equals(i.Tag)).ToArray())
            _menu.Items.Remove(item);
    }

    // Events that arrive while the menu is open do not rebuild it (an open submenu would collapse); the next Opened does.
    public void Insert() => Insert(force: false);

    void Insert(bool force)
    {
        if (_disposed || (_menu.IsOpen && !force)) return;
        Remove();
        var at = 0;
        foreach (var item in Build()) _menu.Items.Insert(at++, item);
    }

    IEnumerable<Control> Build()
    {
        var d = _runtime.Last;
        bool Active(OverrideKind k) => d?.Overrides.Any(o => o.Kind == k) == true;
        var forced = d?.Overrides.FirstOrDefault(o => o.Kind == OverrideKind.ForceWorkload)?.Workload;

        // «Режим: 18 · Батарея, открыт, работа», with «пауза» or «управление у LLT» when its parameters are not applied.
        var state = d is null ? "" : d.Paused ? Tr.T(" · paused") : !_runtime.HasControl ? Tr.T(" · LLT in control") : "";
        yield return new MenuItem
        {
            Header = d is null ? Tr.T("Mode: no data") : Tr.F("Mode: {0}{1}", Ui.ViewModels.StatusViewModel.ProfileTitle(_runtime.Config, d.Profile), state),
            IsEnabled = false, Tag = Tag,
        };
        yield return new MenuItem { Header = _runtime.Devices.ShortText, IsEnabled = false, Tag = Tag };

        var load = new MenuItem { Header = Tr.T("Workload"), Tag = Tag };
        load.Items.Add(Radio(Tr.T("Auto"), forced is null, QuickActionKind.WorkloadAuto));
        load.Items.Add(Radio(Tr.T("Heavy"), forced == Workload.Heavy, QuickActionKind.WorkloadHeavy));
        load.Items.Add(Radio(Tr.T("AI"), forced == Workload.Ai, QuickActionKind.WorkloadAi));
        load.Items.Add(Radio(Tr.T("Work"), forced == Workload.Work, QuickActionKind.WorkloadWork));
        load.Items.Add(Radio(Tr.T("Idle"), forced == Workload.Idle, QuickActionKind.WorkloadIdle));
        yield return load;

        if (d?.Axes.Source == PowerSource.Battery) yield return Toggle(Tr.T("Autonomy"), Active(OverrideKind.Autonomy), QuickActionKind.Autonomy);
        yield return Toggle(Tr.T("Stay awake"), Active(OverrideKind.StayAwake), QuickActionKind.StayAwake);
        yield return Toggle(Tr.T("Rapid charge"), Active(OverrideKind.RapidCharge), QuickActionKind.RapidCharge);
        yield return Toggle(Tr.T("Charge to 100 %"), Active(OverrideKind.ChargeFull), QuickActionKind.ChargeFull);

        var mic = new MenuItem { Header = Tr.T("Microphone"), IsChecked = _runtime.Devices.MicMuted == false, Tag = Tag };   // the check follows the state read after the toggle
        mic.Click += (_, _) => Guard("tray microphone", () => Run(QuickActionKind.Microphone));
        yield return mic;

        var pause = d?.Overrides.FirstOrDefault(o => o.Kind == OverrideKind.Pause);
        var header = Tr.T("Pause");
        if (pause?.Until is { } until)
            header = Tr.F("Pause ({0} min left)", Math.Max(1, (int)Math.Ceiling((until - DateTimeOffset.Now).TotalMinutes)));
        yield return Toggle(header, pause is not null, QuickActionKind.Pause);

        var dgpu = new MenuItem { Header = "dGPU…", IsCheckable = true, IsChecked = Active(OverrideKind.DGpu), IsEnabled = !Active(OverrideKind.DGpu), Tag = Tag };
        dgpu.Click += (_, _) => Guard("tray dGPU", () => Run(QuickActionKind.DGpu));
        yield return dgpu;

        _updates = new MenuItem { Tag = Tag };
        ShowUpdate(_updates);
        _updates.Click += (_, _) => Guard("tray updates", OpenPage);
        yield return _updates;

        yield return new Separator { Tag = Tag };
    }

    void Run(QuickActionKind kind)
    {
        var result = _runtime.Quick(kind);
        if (result.Error is { } error) Notifier.Show(Tr.F("Advanced modes: {0}", error));
    }

    MenuItem Radio(string header, bool selected, QuickActionKind kind)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = selected, Tag = Tag };
        item.Click += (_, _) => Guard("tray workload", () => { Run(kind); item.IsChecked = true; });
        return item;
    }

    MenuItem Toggle(string header, bool active, QuickActionKind kind)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = active, Tag = Tag };
        item.Click += (_, _) => Guard("tray " + kind, () => Run(kind));
        return item;
    }

    // Brings LLT's window forward and opens the plugin's page (its tabs include the updates).
    internal static void OpenPage()
    {
        var window = Application.Current?.MainWindow;
        if (window is null) return;
        LenovoLegionToolkit.WPF.Extensions.WindowExtensions.BringToForeground(window);
        if (window.FindName("_navigationStore") is Wpf.Ui.Controls.NavigationStore store) store.Navigate("legion_modes");
    }

    // «Обновления», bold with the version when a compatible update exists.
    void ShowUpdate(MenuItem item)
    {
        item.Header = _updateVersion is { } v ? Tr.F("Updates · {0} available", v) : Tr.T("Updates");
        item.FontWeight = _updateVersion is null ? FontWeights.Normal : FontWeights.Bold;
    }

    // The flag is read off the UI thread; the item already shown is updated when the answer arrives.
    async Task RefreshUpdateFlagAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var path = Path.Combine(Paths.DataRoot(), "updates.json");
            var file = await Task.Run(() => UpdatesFile.LoadShared(path));
            var version = file?.HasCompatibleUpdate == true ? file.Last?.Release?.Version : null;
            if (version == _updateVersion) return;
            _updateVersion = version;
            if (_updates is not null) ShowUpdate(_updates);
        }
        catch (Exception e) { Log.Error("update flag", e); }
        finally { Volatile.Write(ref _refreshing, 0); }
    }
}
