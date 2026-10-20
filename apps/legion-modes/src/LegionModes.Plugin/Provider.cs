using LegionModes.Core;
using LegionModes.Core.Ipc;
using LegionModes.Plugin.Ui;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Station.Core;
using LenovoLegionToolkit.Lib.Station.Services;

namespace LegionModes.Plugin;

// LLT's extension entry point. LLT creates it with the parameterless constructor and calls Initialize on the UI thread.
public sealed class Provider : IExtensionProvider
{
    Runtime? _runtime;

    public void Initialize(IExtensionContext context)
    {
        try { Log.Init(context.Logger); }
        catch { /* Log is safe before Init */ }

        // Separate blocks: one failing step must not skip the others.
        try { Tr.FromLlt(); }
        catch (Exception e) { Log.Error("language", e); }

        try { Ui.LltTranslations.Apply(); }
        catch (Exception e) { Log.Error("llt translations", e); }

        try { IoCContainer.Resolve<PowerModeFeature>().AllowAllPowerModesOnBattery = true; }
        catch (Exception e) { Log.Error("allow all power modes on battery", e); }

        try
        {
            context.Navigation.Register(new ExtensionNavigationItem
            {
                Id = "legion-modes",
                Title = Tr.T("Расширенные режимы"),
                TitleGetter = () => Tr.T("Расширенные режимы"),   // LLT 2.36: read when the menu is built, so a language change shows
                PageTag = "legion_modes",
                PageType = typeof(ModesPage),
                Icon = ExtensionIcon.Gauge,
            });
        }
        catch (Exception e) { Log.Error("navigation registration", e); }

        try
        {
            var dataDir = context.GetPluginStoragePath(Paths.PluginId);
            var ui = context.UiDispatcher;
            _ = Task.Run(async () =>
            {
                try { _runtime = await Runtime.StartAsync(dataDir, ui); }
                catch (Exception e) { Log.Error("runtime start", e); }
            });
        }
        catch (Exception e) { Log.Error("runtime start", e); }
    }

    public Task ExecuteAsync(string action, params object[] args) => Task.CompletedTask;

    public object? GetData(string key) => null;

    public void SetData(string key, object? value) { }

    public async ValueTask DisposeAsync()
    {
        try { _runtime?.MarkCleanExit(); } catch (Exception e) { Log.Error("clean-exit marker", e); }
        try { await PipeClient.SendAsync(Paths.KeeperPipe, new GracefulExit(), TimeSpan.FromSeconds(2)); } catch { }
        try
        {
            if (_runtime is not null) await _runtime.DisposeAsync();
        }
        catch (Exception e) { Log.Error("runtime dispose", e); }
    }
}
