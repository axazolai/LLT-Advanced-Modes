namespace LegionModes.Core.Model;

// The messages are shown to the owner (the profile editor's footer, Status): Russian, naming the profile and the field
// in the UI's words.
public static class ModesValidator
{
    // Empty = valid. A wrong schema returns only that error.
    public static IReadOnlyList<string> Errors(ModesConfig c, int maxBrightness)
    {
        if (c.SchemaVersion != ModesConfig.CurrentSchema)
            return [Tr.F("schema version {0} is not supported ({1} required)", c.SchemaVersion, ModesConfig.CurrentSchema)];
        if (c.Profiles is null || c.Profiles.Count == 0) return [Tr.T("there are no profiles")];

        var errors = new List<string>();
        if (c.PauseMinutes is < 1 or > 1440) errors.Add(Tr.F("pause length {0} min is outside 1..1440", c.PauseMinutes));

        foreach (var g in c.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.Name)).GroupBy(p => p.Name).Where(g => g.Count() > 1))
            errors.Add(Tr.F("two profiles named \"{0}\"", ProfileNames.DisplayName(g.Key)));
        foreach (var g in c.Profiles.GroupBy(p => p.Id).Where(g => g.Count() > 1))
            errors.Add(Tr.F("profile id {0} is repeated (\"{1}\")", g.Key, string.Join("», «", g.Select(p => ProfileNames.Display(p)))));
        foreach (var g in c.Profiles.SelectMany(p => p.Pipelines ?? []).GroupBy(p => p.Id).Where(g => g.Count() > 1))
            errors.Add(Tr.F("action id {0} is repeated", g.Key));

        var roots = c.Profiles.Count(p => p.Kind == ProfileKind.Root);
        if (roots != 1) errors.Add(Tr.F("there must be exactly one profile \"{0}\", found {1}", ProfileNames.DisplayName(ProfileNames.Root), roots));
        var manuals = c.Profiles.Count(p => p.Kind == ProfileKind.Manual);
        if (manuals != 1) errors.Add(Tr.F("there must be exactly one profile \"{0}\", found {1}", ProfileNames.DisplayName(ProfileNames.Autonomy), manuals));

        for (var i = 0; i < c.Profiles.Count; i++)
        {
            var p = c.Profiles[i];
            var who = $"«{ProfileNames.Display(p)}»";
            if (string.IsNullOrWhiteSpace(p.Name)) errors.Add(Tr.F("profile #{0}: empty name", i));
            if (p.Conditions is null || p.Set is null) { errors.Add(who + ": " + Tr.T("no conditions or parameters")); continue; }

            if (p.DelaySec is < 0 or > 3600) errors.Add(who + ": " + Tr.F("delay {0} s is outside 0..3600", p.DelaySec));
            if (p.HoldSec is < 0 or > 3600) errors.Add(who + ": " + Tr.F("hold {0} s is outside 0..3600", p.HoldSec));

            switch (p.Kind)
            {
                case ProfileKind.Root:
                    if (i != 0) errors.Add(who + ": " + Tr.F("the root profile must be first (now at position {0})", i));
                    if (p.Conditions.Specificity != 0) errors.Add(who + ": " + Tr.T("the root profile cannot have conditions"));
                    if (p.DelaySec != 0 || p.HoldSec != 0) errors.Add(who + ": " + Tr.T("the root profile must have delay and hold of 0"));
                    foreach (var id in Enum.GetValues<ParameterId>())
                        if (!p.Set.IsSet(id)) errors.Add(who + ": " + Tr.F("the root profile must set the parameter \"{0}\"", ParameterName(id)));
                    break;
                case ProfileKind.Manual:
                    if (p.Name != ProfileNames.Autonomy) errors.Add(who + ": " + Tr.F("the manual profile must be named \"{0}\"", ProfileNames.DisplayName(ProfileNames.Autonomy)));
                    if (p.Conditions.Specificity != 0) errors.Add(who + ": " + Tr.T("the manual profile cannot have conditions"));
                    if (p.DelaySec != 0 || p.HoldSec != 0) errors.Add(who + ": " + Tr.T("the manual profile must have delay and hold of 0"));
                    break;
                case ProfileKind.Automatic:
                    if (i == 0) errors.Add(who + ": " + Tr.F("\"{0}\" must be first", ProfileNames.DisplayName(ProfileNames.Root)));
                    if (p.Conditions.Specificity == 0) errors.Add(who + ": " + Tr.T("at least one condition is required"));
                    else
                    {
                        var twin = c.Profiles.Take(i).FirstOrDefault(o => o.Kind == ProfileKind.Automatic && o.Conditions == p.Conditions);
                        if (twin is not null) errors.Add(who + ": " + Tr.F("profile \"{0}\" has the same conditions", ProfileNames.Display(twin)));
                    }
                    break;
                default:
                    errors.Add(who + ": " + Tr.F("unknown profile kind {0}", p.Kind));
                    break;
            }
            ParameterErrors(who, p, maxBrightness, errors);
        }
        return errors;
    }

    // The parameter names of the profile editor.
    public static string ParameterName(ParameterId id) => id switch
    {
        ParameterId.Power => Tr.T("Power and fans"),
        ParameterId.Gpu => Tr.T("GPU mode"),
        ParameterId.Panel => Tr.T("Built-in display refresh rate"),
        ParameterId.External => Tr.T("External monitor"),
        ParameterId.Charging => Tr.T("Charging"),
        ParameterId.Usb => Tr.T("USB in sleep"),
        ParameterId.Processor => Tr.T("Processor and Windows plan"),
        ParameterId.Timers => Tr.T("Idle timers"),
        _ => Tr.T("Lighting"),
    };

    static void ParameterErrors(string who, Profile p, int maxBrightness, List<string> errors)
    {
        var s = p.Set;
        if (s.Panel is { } panel)
        {
            if (panel.Kind == PanelRateKind.Fixed && !FirmwareRanges.PanelHz.Contains(panel.Hz))
                errors.Add(who + ": " + Tr.F("display frequency {0} Hz is not one of {1}", panel.Hz, string.Join(", ", FirmwareRanges.PanelHz)));
            if (panel.Kind == PanelRateKind.Drr && panel.Hz is not (0 or 120))
                errors.Add(who + ": " + Tr.F("DRR works only at 120 Hz, set {0}", panel.Hz));
        }
        if (s.Processor is { Epp: { } epp } && (epp < FirmwareRanges.Epp.Min || epp > FirmwareRanges.Epp.Max))
            errors.Add(who + ": " + Tr.F("EPP {0} is outside {1}..{2}", epp, FirmwareRanges.Epp.Min, FirmwareRanges.Epp.Max));
        if (s.Lighting is { } l)
        {
            if (l.Profile is < 1 or > 6) errors.Add(who + ": " + Tr.F("backlight profile {0} is outside 1..6", l.Profile));
            if (l.Brightness is < 0 or > 9) errors.Add(who + ": " + Tr.F("backlight brightness {0} is outside 0..9", l.Brightness));
        }
        if (s.Timers is { } t) TimerErrors(who, p, t, maxBrightness, errors);
    }

    static void TimerErrors(string who, Profile p, IdleTimers t, int maxBrightness, List<string> errors)
    {
        void Minutes(string field, int v)
        {
            if (v is < 0 or > 1440) errors.Add(who + ": " + Tr.F("{0} {1} min is outside 0..1440", field, v));
        }
        Minutes(Tr.T("display off"), t.DisplayOffMin);
        Minutes(Tr.T("sleep"), t.SleepMin);
        Minutes(Tr.T("hibernation"), t.HibernateMin);
        if (t.Dim is { } dim)
        {
            Minutes(Tr.T("dimming"), dim.Minutes);
            if (dim.Brightness < 0 || dim.Brightness > maxBrightness)
                errors.Add(who + ": " + Tr.F("dimming brightness {0} is outside 0..{1}", dim.Brightness, maxBrightness));
        }
        if (t.ForcedSleepMin is { } forced)
        {
            if (forced < 1 || forced > 1440) errors.Add(who + ": " + Tr.F("sleep with the lid closed {0} min is outside 1..1440", forced));
            if (p.Conditions.Lid != LidState.Closed)
                errors.Add(who + ": " + Tr.T("sleep with the lid closed is set only in a profile with the condition \"closed\""));
        }

        // The stages that are not "never" rise strictly: dim < display off < sleep.
        var stages = new List<(string Name, int Min)>();
        if (t.Dim is { Minutes: > 0 } d) stages.Add((Tr.T("dimming"), d.Minutes));
        if (t.DisplayOffMin > 0) stages.Add((Tr.T("display off"), t.DisplayOffMin));
        if (t.SleepMin > 0) stages.Add((Tr.T("sleep"), t.SleepMin));
        for (var i = 1; i < stages.Count; i++)
            if (stages[i].Min <= stages[i - 1].Min)
                errors.Add(who + ": " + Tr.F("{0} ({1} min) must come before {2} ({3} min)", stages[i - 1].Name, stages[i - 1].Min, stages[i].Name, stages[i].Min));
    }
}
