using LegionModes.Core;
using LegionModes.Core.Ipc;
using LegionModes.Core.Quick;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Utils;

namespace LegionModes.Plugin;

public sealed partial class Runtime
{
    // The tray, the desktop menu and the quick pipe all come through here. Engine actions are applied under the engine's
    // gate and the next tick recalculates at once; the microphone and the dGPU restart are done here on the UI side.
    public QuickResult Quick(QuickActionKind k)
    {
        try
        {
            switch (k)
            {
                case QuickActionKind.Microphone:
                    _ = ToggleMicrophoneAsync();
                    return new QuickResult(true, null);
                case QuickActionKind.DGpu:
                    _ = DGpuAsync();
                    return new QuickResult(true, null);
            }
            QuickResult result;
            lock (_gate)
            {
                result = QuickActions.Apply(_engine, k);
                if (result.Handled && result.Error is null) _immediate = true;
            }
            return result;
        }
        catch (Exception e)
        {
            Log.Error($"quick action {k}", e);
            return new QuickResult(true, e.Message);
        }
    }

    public Task<IReadOnlyList<Core.Programs.WakeRequest>> ReadWakeRequestsAsync() => Apply.WakeRequestsReader.ReadAsync();

    async Task ToggleMicrophoneAsync()
    {
        try
        {
            await Task.Run(async () =>
            {
                var feature = IoCContainer.Resolve<MicrophoneFeature>();
                var state = await feature.GetStateAsync().ConfigureAwait(false);
                await feature.SetStateAsync(state == MicrophoneState.Off ? MicrophoneState.On : MicrophoneState.Off).ConfigureAwait(false);
            }).ConfigureAwait(false);
            await Devices.RefreshAsync(cameraToo: false).ConfigureAwait(false);
        }
        catch (Exception e) { Log.Error("toggle microphone", e); }
    }

    // LLT's window comes forward, the owner confirms, then the machine is put in discrete-only GPU mode and restarted.
    async Task DGpuAsync()
    {
        try
        {
            var confirmed = await _ui.InvokeAsync(async () =>
            {
                if (System.Windows.Application.Current?.MainWindow is { } window) window.BringToForeground();
                return await MessageBoxHelper.ShowAsync("dGPU", Tr.T("Перезагрузить ноутбук в режим dGPU?"), Tr.T("Перезагрузить"), Tr.T("Отмена"));
            }).ConfigureAwait(false);
            if (!await confirmed.ConfigureAwait(false)) return;
            var error = await Task.Run(Apply.Applier.ApplyDGpuOverrideAndRestartAsync).ConfigureAwait(false);
            if (error is not null)
            {
                Status.LastApplyError = "dGPU: " + error;
                Shell.Notifier.Show(Tr.F("Режимы: не удалось включить режим dGPU: {0}", error));
            }
        }
        catch (Exception e) { Log.Error("dGPU action", e); }
    }
}
