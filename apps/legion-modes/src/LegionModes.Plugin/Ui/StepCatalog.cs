using LegionModes.Plugin.Pipelines;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Automation.Steps;
using LenovoLegionToolkit.Lib.Station.Services;

namespace LegionModes.Plugin.Ui;

// The steps the pipeline constructor offers: LLT's built-in list as AutomationPage.xaml.cs:168-219 builds it (default
// arguments) plus registered extension steps, minus ExcludedSteps, minus those whose IsSupportedAsync is false or throws.
// Total = the count before the support filter (AutomationPipelineControl shows "N of Total").
internal static class StepCatalog
{
    static Task<(IAutomationStep[] Steps, int Total)>? _cached;

    public static Task<(IAutomationStep[] Steps, int Total)> GetAsync() => _cached ??= BuildAsync();

    static async Task<(IAutomationStep[] Steps, int Total)> BuildAsync()
    {
        Func<IAutomationStep>[] factories =
        [
            () => new AIEngineAutomationStep(default),
            () => new AlwaysOnUsbAutomationStep(default),
            () => new BatteryAutomationStep(default),
            () => new BatteryNightChargeAutomationStep(default),
            () => new DeactivateGPUAutomationStep(default),
            () => new DelayAutomationStep(default),
            () => new DisplayBrightnessAutomationStep(50),
            () => new DpiScaleAutomationStep(default),
            () => new FlipToStartAutomationStep(default),
            () => new FnLockAutomationStep(default),
            () => new CycleGodModePresetAutomationStep(),
            () => new GodModePresetAutomationStep(default),
            () => new HardwareSensorsAutomationStep(default),
            () => new HDRAutomationStep(default),
            () => new AutoColorManagementAutomationStep(default),
            () => new ITSModeAutomationStep(default),
            () => new HybridModeAutomationStep(default),
            () => new InstantBootAutomationStep(default),
            () => new MacroAutomationStep(default),
            () => new MicrophoneAutomationStep(default),
            () => new SpeakerAutomationStep(default),
            () => new SpeakerVolumeAutomationStep(default),
            () => new NotificationAutomationStep(default),
            () => new OneLevelWhiteKeyboardBacklightAutomationStep(default),
            () => new OverclockDiscreteGPUAutomationStep(default),
            () => new OverDriveAutomationStep(default),
            () => new PanelLogoBacklightAutomationStep(default),
            () => new PlaySoundAutomationStep(default),
            () => new PortsBacklightAutomationStep(default),
            () => new PowerModeAutomationStep(default),
            () => new QuickActionAutomationStep(default),
            () => new RefreshRateAutomationStep(default),
            () => new ResolutionAutomationStep(default),
            () => new RGBKeyboardBacklightAutomationStep(default),
            () => new RunAutomationStep(default, default, default, default, default),
            () => new SpectrumKeyboardBacklightBrightnessAutomationStep(0),
            () => new SpectrumKeyboardBacklightProfileAutomationStep(1),
            () => new SpectrumKeyboardBacklightImportProfileAutomationStep(default),
            () => new TouchpadLockAutomationStep(default),
            () => new TurnOffMonitorsAutomationStep(),
            () => new WiFiAutomationStep(default),
            () => new AirplaneModeAutomationStep(default),
            () => new WhiteKeyboardBacklightAutomationStep(default),
            () => new WinKeyAutomationStep(default),
            () => new CloseAppAutomationStep(),
            () => new ShowAppAutomationStep(),
            () => new OsdAutomationStep(default),
            () => new OsdLockPositionAutomationStep(default),
            () => new FanMaxSpeedAutomationStep(default),
        ];
        var steps = new List<IAutomationStep>();
        foreach (var make in factories)
        {
            try { steps.Add(make()); }                   // a step whose constructor needs a service LLT lacks is left out
            catch (Exception e) { Log.Trace("step not created: " + e.Message); }
        }
        try
        {
            foreach (var ext in IoCContainer.Resolve<IAutomationStepRegistry>().Steps)
                if (ext.Factory() is IAutomationStep step) steps.Add(step);
        }
        catch (Exception e) { Log.Error("step registry", e); }

        steps.RemoveAll(s => ExcludedSteps.Types.Contains(s.GetType()));
        var total = steps.Count;
        var supported = new List<IAutomationStep>();
        foreach (var s in steps)
        {
            try { if (await s.IsSupportedAsync()) supported.Add(s); }
            catch (Exception e) { Log.Trace($"step {s.GetType().Name} support check failed: {e.Message}"); }
        }
        return (supported.ToArray(), total);
    }
}
