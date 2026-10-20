using System.Security.Cryptography;
using System.Text;

namespace LegionModes.Core.Model;

// refs/mode-defaults.md sections 2-6, in list order. Every value is TRIAL until Task 30.
public static class FactoryProfiles
{
    const PowerSource Ac = PowerSource.Ac, Weak = PowerSource.Weak, Bat = PowerSource.Battery;
    const LidState Open = LidState.Open, Closed = LidState.Closed;
    const Workload Heavy = Workload.Heavy, Ai = Workload.Ai, Work = Workload.Work, Idle = Workload.Idle;

    static Guid IdOf(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes("LegionModes/" + key)));

    static readonly Guid OllamaId = IdOf("pipeline/ollama");

    const string OllamaPipelineTemplate = """
        {"Id":"{ID}","Name":"Перезапуск ollama serve","Trigger":null,"IsExclusive":false,"RunOnStartup":false,
         "Steps":[{"$type":"LenovoLegionToolkit.Lib.Automation.Steps.RunAutomationStep, LenovoLegionToolkit.Lib.Automation",
          "ScriptPath":"powershell.exe",
          "ScriptArguments":"-NoProfile -WindowStyle Hidden -Command \"Get-Process ollama -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Process ollama -ArgumentList serve -WindowStyle Hidden\"",
          "RunSilently":true,"WaitUntilFinished":false,"CheckInstance":false}]}
        """;

    // ---- section 3: own presets, by profile name ----
    static PowerPreset Preset(int l, int s, int p, int cross, int cpuT, int boost, int tgp, int gpuT, int tpp, params int[] curve) =>
        new(l, s, p, cross, cpuT, 0, boost, tgp, gpuT, tpp, curve.Select((v, i) => Math.Max(v, FirmwareRanges.FanMinimum[i])).ToArray(), false, 0, 0);   // never below the firmware floor

    static readonly Dictionary<string, PowerPreset> PresetTable = new()
    {
        ["Всегда"] = Preset(70, 90, 110, 55, 90, 0, 80, 80, 35, 2, 2, 2, 2, 3, 4, 5, 7, 9, 10),
        ["Слабый адаптер"] = Preset(55, 65, 75, 35, 90, 0, 80, 75, 35, 1, 1, 1, 1, 2, 3, 4, 6, 8, 10),
        ["Батарея"] = Preset(55, 65, 75, 35, 90, 0, 80, 75, 35, 1, 1, 1, 1, 2, 3, 4, 6, 8, 10),
        ["Сеть, закрыт"] = Preset(65, 85, 105, 50, 90, 0, 80, 78, 35, 2, 2, 2, 3, 4, 5, 6, 8, 9, 10),
        ["Сеть, открыт, тяжёлая"] = Preset(90, 110, 130, 65, 90, 15, 130, 83, 60, 3, 3, 3, 3, 5, 6, 7, 8, 9, 10),
        ["Сеть, открыт, ИИ"] = Preset(60, 80, 100, 45, 90, 0, 110, 80, 50, 3, 3, 3, 3, 5, 6, 8, 9, 10, 10),
        ["Сеть, открыт, простой"] = Preset(50, 60, 70, 30, 85, 0, 80, 75, 35, 1, 1, 1, 1, 1, 2, 3, 5, 7, 10),
        ["Сеть, закрыт, тяжёлая"] = Preset(80, 100, 120, 55, 90, 15, 115, 80, 50, 4, 4, 4, 4, 6, 7, 8, 9, 10, 10),
        ["Сеть, закрыт, простой"] = Preset(50, 60, 70, 30, 85, 0, 80, 75, 35, 1, 1, 1, 1, 2, 3, 4, 6, 8, 10),
        ["Слабый адаптер, открыт, тяжёлая"] = Preset(60, 70, 85, 40, 90, 0, 80, 80, 35, 2, 2, 2, 2, 3, 5, 6, 8, 9, 10),
        ["Слабый адаптер, открыт, простой"] = Preset(50, 60, 70, 30, 85, 0, 80, 75, 35, 1, 1, 1, 1, 1, 2, 3, 5, 7, 10),
        ["Батарея, открыт, тяжёлая"] = Preset(55, 70, 90, 45, 90, 0, 80, 80, 35, 2, 2, 2, 2, 3, 5, 6, 8, 9, 10),
        ["Батарея, открыт, простой"] = Preset(50, 60, 70, 30, 85, 0, 80, 75, 35, 1, 1, 1, 1, 1, 2, 3, 5, 7, 10),
        ["Батарея, закрыт, тяжёлая"] = Preset(55, 65, 75, 40, 90, 0, 80, 78, 35, 3, 3, 3, 3, 5, 6, 7, 8, 10, 10),
        ["Батарея, закрыт, простой"] = Preset(50, 60, 70, 30, 85, 0, 80, 75, 35, 1, 1, 1, 1, 2, 3, 4, 6, 8, 10),
        ["Автономия"] = Preset(50, 60, 70, 30, 85, 0, 80, 75, 35, 1, 1, 1, 1, 1, 2, 3, 5, 7, 10),
    };

    public static IReadOnlyDictionary<string, PowerPreset> Presets() => PresetTable;

    // ---- section 4 building blocks ----
    static readonly ExternalRate Max = new(ExternalRateKind.Maximum), Fixed60 = new(ExternalRateKind.Fixed, 60);
    static readonly PanelRate Panel60 = new(PanelRateKind.Fixed, 60);
    static readonly ProcessorScheme Saver = new(BoostMode.Disabled, 100, WindowsPowerMode.BestPowerEfficiency);
    static readonly ProcessorScheme Balanced50 = new(BoostMode.Enabled, 50, WindowsPowerMode.Balanced);
    static readonly Lighting Dark = new(3, 0);
    static readonly Lighting SaverOff = new(4, 0);   // Spectrum slot 4: the charge-saving slot
    static readonly Lighting SaverDim = new(4, 1);

    // section 5: Timers(dim, display off, sleep, hibernate, forced sleep); minutes, 0 = never
    static IdleTimers Timers(DimStage? dim, int off, int sleep, int hibernate, int? forced = null) =>
        new(dim, off, sleep, hibernate, forced);

    // The name in both languages of the built-in set (Russian is the name the profile is known by).
    static Dictionary<string, string> NamesOf(string ru) =>
        new() { ["ru"] = ru, ["en"] = ProfileNames.BuiltInEnglish(ru) ?? throw new InvalidOperationException("no English name for " + ru) };

    static Profile Auto(string name, Conditions c, int hold, Parameters set, Pipeline? pipeline = null) =>
        new(IdOf(name), name, ProfileKind.Automatic, c, 0, hold, set,
            pipeline is null ? [] : [pipeline], pipeline is null ? [] : [new PipelineSlot(pipeline.Id, true)], NamesOf(name));

    static Parameters Own(Parameters p) => p with { Power = PowerSetting.Own };

    public static ModesConfig Create()
    {
        var ollama = new Pipeline(OllamaId, PipelineKeys.Enter, OllamaPipelineTemplate.Replace("{ID}", OllamaId.ToString()));
        var none = new Parameters();
        Profile[] profiles =
        [
            new(IdOf(ProfileNames.Root), ProfileNames.Root, ProfileKind.Root, new Conditions(), 0, 0,
                new Parameters(PowerSetting.Own, GpuMode.HybridIgpuOnly, new PanelRate(PanelRateKind.Drr),
                    new ExternalMonitor(Projection.Extend, Max), ChargingMode.Conservation, UsbPower.Always,
                    new ProcessorScheme(BoostMode.Enabled, 33, WindowsPowerMode.Balanced),
                    Timers(new DimStage(5, 20), 10, 30, 0), new Lighting(3, 3)), [], [], NamesOf(ProfileNames.Root)),

            Auto("Закрыт", new(Lid: Closed), 0, new Parameters(
                Panel: new PanelRate(PanelRateKind.DoNotTouch), External: new ExternalMonitor(Projection.ExternalOnly, Max),
                Timers: Timers(null, 10, 30, 0, 1), Lighting: Dark)),

            Auto("Слабый адаптер", new(Source: Weak), 0, Own(new Parameters(
                Panel: Panel60, External: new ExternalMonitor(Projection.Extend, Fixed60), Charging: ChargingMode.Normal,
                Usb: UsbPower.WhenSleeping, Processor: new ProcessorScheme(BoostMode.Enabled, 50, WindowsPowerMode.Balanced),
                Timers: Timers(new DimStage(3, 20), 5, 20, 180), Lighting: SaverDim))),

            Auto("Батарея", new(Source: Bat), 0, Own(new Parameters(
                Panel: Panel60, External: new ExternalMonitor(Projection.Extend, Fixed60), Charging: ChargingMode.DoNotTouch,
                Usb: UsbPower.Off, Processor: new ProcessorScheme(BoostMode.Enabled, 80, WindowsPowerMode.BestPowerEfficiency),
                Timers: Timers(new DimStage(3, 15), 5, 15, 60), Lighting: SaverDim))),

            Auto("Сеть, закрыт", new(Ac, Closed), 0, Own(none)),

            Auto("Слабый адаптер, закрыт", new(Weak, Closed), 0, new Parameters(
                External: new ExternalMonitor(Projection.ExternalOnly, Fixed60), Timers: Timers(null, 5, 20, 180, 1))),

            Auto("Батарея, закрыт", new(Bat, Closed), 0, new Parameters(
                External: new ExternalMonitor(Projection.ExternalOnly, Fixed60), Timers: Timers(null, 5, 15, 30, 1))),

            Auto("Сеть, открыт, тяжёлая", new(Ac, Open, Heavy), 60, Own(new Parameters(
                Gpu: GpuMode.Hybrid, Panel: new PanelRate(PanelRateKind.Fixed, 240), Timers: Timers(new DimStage(10, 20), 15, 60, 0), Lighting: new Lighting(2, 3)))),

            Auto("Сеть, открыт, ИИ", new(Ac, Open, Ai), 60, Own(new Parameters(
                Gpu: GpuMode.Hybrid, Timers: Timers(new DimStage(5, 20), 10, 0, 0))), ollama),

            Auto("Сеть, открыт, работа", new(Ac, Open, Work), 60, new Parameters(Lighting: new Lighting(1, 3))),

            Auto("Сеть, открыт, простой", new(Ac, Open, Idle), 0, Own(none)),

            Auto("Сеть, закрыт, тяжёлая", new(Ac, Closed, Heavy), 60, Own(new Parameters(
                Gpu: GpuMode.Hybrid, Timers: Timers(null, 15, 60, 0, 1)))),

            Auto("Сеть, закрыт, работа", new(Ac, Closed, Work), 60, none),

            Auto("Сеть, закрыт, простой", new(Ac, Closed, Idle), 0, Own(none)),

            Auto("Слабый адаптер, открыт, тяжёлая", new(Weak, Open, Heavy), 60, Own(new Parameters(
                Gpu: GpuMode.Hybrid, Panel: new PanelRate(PanelRateKind.Fixed, 120)))),

            Auto("Слабый адаптер, открыт, работа", new(Weak, Open, Work), 60, none),

            Auto("Слабый адаптер, открыт, простой", new(Weak, Open, Idle), 0, Own(new Parameters(Processor: Saver))),

            Auto("Батарея, открыт, тяжёлая", new(Bat, Open, Heavy), 60, Own(new Parameters(
                Gpu: GpuMode.Hybrid, Processor: Balanced50))),

            Auto("Батарея, открыт, работа", new(Bat, Open, Work), 60, none),

            Auto("Батарея, открыт, простой", new(Bat, Open, Idle), 0, Own(new Parameters(
                Processor: Saver, Timers: Timers(new DimStage(1, 15), 3, 10, 60), Lighting: SaverOff))),

            Auto("Батарея, закрыт, тяжёлая", new(Bat, Closed, Heavy), 60, Own(new Parameters(
                Gpu: GpuMode.Hybrid, Processor: Balanced50))),

            Auto("Батарея, закрыт, работа", new(Bat, Closed, Work), 60, none),

            Auto("Батарея, закрыт, простой", new(Bat, Closed, Idle), 0, Own(new Parameters(
                Processor: Saver, Timers: Timers(null, 3, 10, 30, 1)))),

            new(IdOf(ProfileNames.Autonomy), ProfileNames.Autonomy, ProfileKind.Manual, new Conditions(), 0, 0,
                Own(new Parameters(Gpu: GpuMode.HybridIgpuOnly, Panel: Panel60,
                    External: new ExternalMonitor(Projection.Extend, Fixed60), Usb: UsbPower.Off, Processor: Saver,
                    Timers: Timers(new DimStage(0, 30), 3, 10, 60), Lighting: SaverOff)), [], [], NamesOf(ProfileNames.Autonomy)),
        ];
        return new ModesConfig(ModesConfig.CurrentSchema, profiles);
    }
}
