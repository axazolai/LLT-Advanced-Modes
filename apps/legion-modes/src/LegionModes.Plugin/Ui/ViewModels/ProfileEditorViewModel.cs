using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Model;

namespace LegionModes.Plugin.Ui.ViewModels;

// A draft of one profile. Inheritance, overlaps and validation are computed on the config with the draft in place of the
// saved profile, so the editor shows what saving would give.
public sealed class ProfileEditorViewModel
{
    readonly ProfilesViewModel _list;

    public ProfileEditorViewModel(ProfilesViewModel list, Profile original)
    {
        _list = list;
        Original = original;
        Name = ProfileNames.Display(original);   // the name in the current language
        Conditions = original.Conditions;
        DelaySec = original.DelaySec;
        HoldSec = original.HoldSec;
        Set = original.Set;
    }

    public event Action? Changed;

    public Profile Original { get; }
    public string Name { get; private set; }
    public Conditions Conditions { get; private set; }
    public int DelaySec { get; private set; }
    public int HoldSec { get; private set; }
    public Parameters Set { get; private set; }
    public bool IsAutomatic => Original.Kind == ProfileKind.Automatic;
    public bool IsRoot => Original.Kind == ProfileKind.Root;
    public int MaxBrightness => _list.MaxBrightness;
    public IModesHost Host => _list.Host;
    public ProfilesViewModel List => _list;

    // The name box holds the name of the current language; a changed one is kept under that language (ProfileNames.WithName).
    public Profile Draft => (Name == ProfileNames.Display(Original) ? Original : ProfileNames.WithName(Original, Name))
        with { Conditions = Conditions, DelaySec = DelaySec, HoldSec = HoldSec, Set = Set };
    public ModesConfig DraftConfig => _list.Config with { Profiles = _list.Config.Profiles.Select(p => p.Id == Original.Id ? Draft : p).ToList() };
    public int Index => _list.Config.IndexOf(Original);
    public bool Dirty => Draft != Original;

    public EffectiveParameters Effective => Resolver.Effective(DraftConfig, Draft);
    public IReadOnlyList<Overlap> Overlaps => IsAutomatic ? Resolver.Overlaps(DraftConfig, Draft) : [];
    public IReadOnlyList<EffectivePipeline> Pipelines => Resolver.Pipelines(DraftConfig, Draft);

    // The value in force for this parameter and the profile it comes from.
    public (object Value, Profile Source) EffectiveOf(ParameterId id)
    {
        var e = Effective;
        return id switch
        {
            ParameterId.Power => (e.Power.Value, e.Power.Source),
            ParameterId.Gpu => (e.Gpu.Value, e.Gpu.Source),
            ParameterId.Panel => (e.Panel.Value, e.Panel.Source),
            ParameterId.External => (e.External.Value, e.External.Source),
            ParameterId.Charging => (e.Charging.Value, e.Charging.Source),
            ParameterId.Usb => (e.Usb.Value, e.Usb.Source),
            ParameterId.Processor => (e.Processor.Value, e.Processor.Source),
            ParameterId.Timers => (e.Timers.Value, e.Timers.Source),
            _ => (e.Lighting.Value, e.Lighting.Source),
        };
    }

    public void SetName(string name) { Name = name; Changed?.Invoke(); }
    public void SetConditions(Conditions c) { Conditions = c; Changed?.Invoke(); }
    public void SetDelay(int s) { DelaySec = s; Changed?.Invoke(); }
    public void SetHold(int s) { HoldSec = s; Changed?.Invoke(); }

    public void SetParameter(ParameterId id, object? value)
    {
        Set = id switch
        {
            ParameterId.Power => Set with { Power = (PowerSetting?)value },
            ParameterId.Gpu => Set with { Gpu = (GpuMode?)value },
            ParameterId.Panel => Set with { Panel = (PanelRate?)value },
            ParameterId.External => Set with { External = (ExternalMonitor?)value },
            ParameterId.Charging => Set with { Charging = (ChargingMode?)value },
            ParameterId.Usb => Set with { Usb = (UsbPower?)value },
            ParameterId.Processor => Set with { Processor = (ProcessorScheme?)value },
            ParameterId.Timers => Set with { Timers = (IdleTimers?)value },
            _ => Set with { Lighting = (Lighting?)value },
        };
        Changed?.Invoke();
    }

    // «Задать здесь»: the inherited value becomes the profile's own starting point.
    public void Own(ParameterId id) => SetParameter(id, EffectiveOf(id).Value);

    // «Вернуть унаследованное».
    public void Inherit(ParameterId id) => SetParameter(id, null);

    public IReadOnlyList<string> Errors
    {
        get
        {
            var errors = ModesValidator.Errors(DraftConfig, MaxBrightness);
            // The validator quotes profile names: «X».
            var mine = errors.Where(e => e.Contains($"«{Name}»", StringComparison.Ordinal) || e.Contains($"«{ProfileNames.Display(Original)}»", StringComparison.Ordinal)).ToList();
            return mine.Count > 0 ? mine : errors;
        }
    }

    public Task<string?> SaveAsync() => _list.TrySaveAsync(DraftConfig, Tr.F("Profile \"{0}\" saved", Name));
}
