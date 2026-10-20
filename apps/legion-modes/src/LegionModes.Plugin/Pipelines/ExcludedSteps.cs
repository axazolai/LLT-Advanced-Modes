using LenovoLegionToolkit.Lib.Automation.Steps;

namespace LegionModes.Plugin.Pipelines;

// Steps the constructor does not offer: those that write a profile parameter (spec section 4) and QuickAction, which
// expands against LLT's own pipelines.
internal static class ExcludedSteps
{
    public static readonly Type[] Types =
    [
        typeof(PowerModeAutomationStep), typeof(GodModePresetAutomationStep), typeof(CycleGodModePresetAutomationStep),
        typeof(HybridModeAutomationStep), typeof(RefreshRateAutomationStep), typeof(DisplayBrightnessAutomationStep),
        typeof(SpectrumKeyboardBacklightProfileAutomationStep), typeof(SpectrumKeyboardBacklightBrightnessAutomationStep),
        typeof(SpectrumKeyboardBacklightImportProfileAutomationStep), typeof(PanelLogoBacklightAutomationStep),
        typeof(PortsBacklightAutomationStep), typeof(AlwaysOnUsbAutomationStep), typeof(BatteryAutomationStep),
        typeof(BatteryNightChargeAutomationStep), typeof(QuickActionAutomationStep),
    ];
}
