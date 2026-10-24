using System.Windows;
using System.Windows.Threading;
using LegionModes.Core;
using LegionModes.Core.Model;
using LegionModes.Plugin.Ui.ViewModels;
using Wpf.Ui.Controls;

namespace LegionModes.Plugin.Ui;

// "Режимы": five tabs over one ModesDocument. The page waits for the runtime (it starts on a background task), then binds.
public partial class ModesPage : UiPage
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    IModesHost? _host;
    StatusViewModel? _status;
    ControlViewModel? _control;
    UpdatesViewModel? _updates;
    ProfilesViewModel? _profiles;
    int _ticks;
    Guid? _shownInForce;
    bool _subscribed;
    bool _binding;
    bool _bindFailed;

    public ModesPage()
    {
        Resources.MergedDictionaries.Add(SharedResources.Instance);   // before InitializeComponent: StaticResource is resolved while loading
        InitializeComponent();
        Tr.FromLlt();   // a page built right after a language change must not wait for the file watcher
        Title = Tr.T("Advanced modes");
        _waiting.Text = Tr.T("The plugin is starting…");
        _inForceLabel.Text = Tr.T("In force now|2");
        _effectiveButton.Content = Tr.T("Effective actions");
        string[] tabNames = [Tr.T("Status"), Tr.T("Profiles"), Tr.T("Programs"), Tr.T("Updates"), Tr.T("Control")];
        for (var i = 0; i < tabNames.Length; i++) ((System.Windows.Controls.TabItem)_tabs.Items[i]).Header = tabNames[i];
        FrameworkElement[] pages = [_statusTab, _profilesTab, _programsTab, _updatesTab, _controlTab];
        for (var i = 0; i < pages.Length; i++) ((System.Windows.Controls.TabItem)_tabs.Items[i]).Tag = pages[i];
        _tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, _tabs)) return;
            Guard("tab change", () =>
            {
                foreach (var p in pages) p.Visibility = IsTabSelected(p) ? Visibility.Visible : Visibility.Collapsed;
                _inForceBar.Visibility = IsTabSelected(_profilesTab) ? Visibility.Visible : Visibility.Collapsed;
                if (IsTabSelected(_statusTab)) _ = _status?.RefreshWakeAsync();
            });
        };
        _tabs.SelectedIndex = 0;
        _effectiveButton.Click += (_, _) => Guard("effective actions", OpenEffectiveActions);
        _statusTab.EffectiveActionsRequested += () => Guard("effective actions", OpenEffectiveActions);
        _timer.Tick += (_, _) => Guard("page timer", OnTick);
        Loaded += (_, _) => Guard("page loaded", OnLoaded);
        Unloaded += (_, _) => Guard("page unloaded", OnUnloaded);
    }

    static void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Error(what, e); }
    }

    void OpenEffectiveActions()
    {
        if (_profiles is not null && _host?.Last is { } d) EffectiveActionsWindow.Open(Window.GetWindow(this), _profiles.Config, d.Profile);
    }

    void OnLoaded()
    {
        _timer.Start();
        _bindFailed = false;          // a failed build is retried on the next Loaded
        if (_host is null) _ = TryBindAsync();
        else Subscribe();
    }

    void OnUnloaded()
    {
        _timer.Stop();
        if (_host is null || !_subscribed) return;
        _host.Decided -= OnDecisionChanged;
        _host.Status.PropertyChanged -= OnStatusChanged;
        _host.Devices.PropertyChanged -= OnStatusChanged;
        _subscribed = false;
    }

    void OnTick()
    {
        if (_host is null) { if (!_bindFailed) _ = TryBindAsync(); return; }
        _status?.Refresh();
        _control?.RefreshState();
        if (++_ticks % 10 != 0) return;
        if (_updates is not null && IsTabSelected(_updatesTab)) _ = _updates.RefreshAsync();
        if (_status is not null && IsTabSelected(_statusTab)) _ = _status.RefreshWakeAsync();
    }

    bool IsTabSelected(FrameworkElement content) => _tabs.SelectedItem is System.Windows.Controls.TabItem t && ReferenceEquals(t.Tag, content);

    async Task TryBindAsync()
    {
        if (_binding) return;
        var runtime = ModesHost.Source();
        if (runtime is null) return;
        _binding = true;
        try
        {
            var doc = await Task.Run(() => ModesDocument.Load(runtime));   // reads both files off the UI thread
            var dialogs = new Dialogs(this);
            var status = new StatusViewModel(runtime);
            var control = new ControlViewModel(doc, dialogs);
            var updates = new UpdatesViewModel();
            var programs = new ProgramsViewModel(doc);
            _profiles = new ProfilesViewModel(doc, runtime, dialogs);
            _profilesTab.Bind(_profiles);
            _status = status;
            _control = control;
            _updates = updates;
            _statusTab.Bind(status);
            _programsTab.Bind(programs);
            _updatesTab.Bind(updates);
            _controlTab.Bind(control);
            _host = runtime;
            _waiting.Visibility = Visibility.Collapsed;
            _body.Visibility = Visibility.Visible;
            ShowInForce();
            Subscribe();
            _ = updates.RefreshAsync();
            if (IsTabSelected(_statusTab)) _ = status.RefreshWakeAsync();
        }
        catch (Exception e)
        {
            _bindFailed = true;
            _host = null;
            Log.Error("page bind", e);
            _waiting.Text = Tr.F("Could not open the page: {0}", e.Message);
        }
        finally { _binding = false; }
    }

    void Subscribe()
    {
        if (_host is null || _subscribed) return;
        _host.Decided += OnDecisionChanged;
        _host.Status.PropertyChanged += OnStatusChanged;
        _host.Devices.PropertyChanged += OnStatusChanged;   // posted on the UI dispatcher by DeviceStatus
        _subscribed = true;
        _status?.Refresh();
        _control?.RefreshState();
    }

    // Decided is raised on the UI dispatcher.
    void OnDecisionChanged(Core.Engine.Decision e)
    {
        try
        {
            _ = Dispatcher.BeginInvoke(() => Guard("decision refresh", () =>
            {
                var moved = _profiles is not null && _shownInForce != e.Profile.Id;
                _shownInForce = e.Profile.Id;
                _status?.Refresh();
                ShowInForce();
                if (moved) _profilesTab.RefreshInForce();
            }));
        }
        catch (Exception ex) { Log.Error("decision changed", ex); }
    }

    void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Guard("status refresh", () =>
        {
            _status?.Refresh();
            _control?.RefreshState();
            ShowInForce();
        });
    }

    // "Сейчас действует  7 · Батарея, открыт, работа  условия выполнены"; without control the profile is not applied.
    void ShowInForce()
    {
        if (_host is null) return;
        var d = _host.Last;
        if (d is null)
        {
            _inForceName.Text = "—";
            _inForceReason.Text = Tr.T("no profile selected yet");
            return;
        }
        var index = _host.Config.IndexOf(d.Profile);
        _inForceName.Text = (index >= 0 ? $"{index} · " : "") + ProfileNames.Display(d.Profile);
        _inForceReason.Text = d.Paused ? Tr.F("{0} · paused, parameters are not applied", d.Reason)
            : !_host.HasControl ? Tr.F("{0} · LLT has control, parameters are not applied", d.Reason)
            : d.Reason;
        _inForceIcon.Symbol = d.Paused || !_host.HasControl ? Wpf.Ui.Common.SymbolRegular.PauseCircle24 : Wpf.Ui.Common.SymbolRegular.CheckmarkCircle24;
        _inForceIcon.SetResourceReference(ForegroundProperty, d.Paused || !_host.HasControl ? "PaletteOrangeBrush" : "PaletteGreenBrush");
    }
}
