namespace LegionModes.Core.Model;

// The messages are shown to the owner (the profile editor's footer, Status): Russian, naming the profile and the field
// in the UI's words.
public static class ModesValidator
{
    // Empty = valid. A wrong schema returns only that error.
    public static IReadOnlyList<string> Errors(ModesConfig c, int maxBrightness)
    {
        if (c.SchemaVersion != ModesConfig.CurrentSchema)
            return [Tr.F("версия схемы {0} не поддерживается (нужна {1})", c.SchemaVersion, ModesConfig.CurrentSchema)];
        if (c.Profiles is null || c.Profiles.Count == 0) return [Tr.T("нет ни одного профиля")];

        var errors = new List<string>();
        if (c.PauseMinutes is < 1 or > 1440) errors.Add(Tr.F("длительность паузы {0} мин вне 1..1440", c.PauseMinutes));

        foreach (var g in c.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.Name)).GroupBy(p => p.Name).Where(g => g.Count() > 1))
            errors.Add(Tr.F("два профиля с названием «{0}»", Tr.T(g.Key)));
        foreach (var g in c.Profiles.GroupBy(p => p.Id).Where(g => g.Count() > 1))
            errors.Add(Tr.F("повторяется id профиля {0} («{1}»)", g.Key, string.Join("», «", g.Select(p => ProfileNames.Display(p)))));
        foreach (var g in c.Profiles.SelectMany(p => p.Pipelines ?? []).GroupBy(p => p.Id).Where(g => g.Count() > 1))
            errors.Add(Tr.F("повторяется id действия {0}", g.Key));

        var roots = c.Profiles.Count(p => p.Kind == ProfileKind.Root);
        if (roots != 1) errors.Add(Tr.F("профиль «{0}» должен быть ровно один, найдено {1}", ProfileNames.DisplayName(ProfileNames.Root), roots));
        var manuals = c.Profiles.Count(p => p.Kind == ProfileKind.Manual);
        if (manuals != 1) errors.Add(Tr.F("профиль «{0}» должен быть ровно один, найдено {1}", ProfileNames.DisplayName(ProfileNames.Autonomy), manuals));

        for (var i = 0; i < c.Profiles.Count; i++)
        {
            var p = c.Profiles[i];
            var who = $"«{ProfileNames.Display(p)}»";
            if (string.IsNullOrWhiteSpace(p.Name)) errors.Add(Tr.F("профиль №{0}: пустое название", i));
            if (p.Conditions is null || p.Set is null) { errors.Add(who + ": " + Tr.T("нет условий или параметров")); continue; }

            if (p.DelaySec is < 0 or > 3600) errors.Add(who + ": " + Tr.F("задержка {0} с вне 0..3600", p.DelaySec));
            if (p.HoldSec is < 0 or > 3600) errors.Add(who + ": " + Tr.F("удержание {0} с вне 0..3600", p.HoldSec));

            switch (p.Kind)
            {
                case ProfileKind.Root:
                    if (i != 0) errors.Add(who + ": " + Tr.F("корневой профиль должен стоять первым (сейчас на месте {0})", i));
                    if (p.Conditions.Specificity != 0) errors.Add(who + ": " + Tr.T("у корневого профиля не может быть условий"));
                    if (p.DelaySec != 0 || p.HoldSec != 0) errors.Add(who + ": " + Tr.T("у корневого профиля задержка и удержание должны быть 0"));
                    foreach (var id in Enum.GetValues<ParameterId>())
                        if (!p.Set.IsSet(id)) errors.Add(who + ": " + Tr.F("корневой профиль должен задавать параметр «{0}»", ParameterName(id)));
                    break;
                case ProfileKind.Manual:
                    if (p.Name != ProfileNames.Autonomy) errors.Add(who + ": " + Tr.F("ручной профиль должен называться «{0}»", ProfileNames.DisplayName(ProfileNames.Autonomy)));
                    if (p.Conditions.Specificity != 0) errors.Add(who + ": " + Tr.T("у ручного профиля не может быть условий"));
                    if (p.DelaySec != 0 || p.HoldSec != 0) errors.Add(who + ": " + Tr.T("у ручного профиля задержка и удержание должны быть 0"));
                    break;
                case ProfileKind.Automatic:
                    if (i == 0) errors.Add(who + ": " + Tr.F("первым должен стоять «{0}»", ProfileNames.DisplayName(ProfileNames.Root)));
                    if (p.Conditions.Specificity == 0) errors.Add(who + ": " + Tr.T("нужно хотя бы одно условие"));
                    else
                    {
                        var twin = c.Profiles.Take(i).FirstOrDefault(o => o.Kind == ProfileKind.Automatic && o.Conditions == p.Conditions);
                        if (twin is not null) errors.Add(who + ": " + Tr.F("такие же условия у профиля «{0}»", ProfileNames.Display(twin)));
                    }
                    break;
                default:
                    errors.Add(who + ": " + Tr.F("неизвестный вид профиля {0}", p.Kind));
                    break;
            }
            ParameterErrors(who, p, maxBrightness, errors);
        }
        return errors;
    }

    // The parameter names of the profile editor.
    public static string ParameterName(ParameterId id) => id switch
    {
        ParameterId.Power => Tr.T("Мощность и вентиляторы"),
        ParameterId.Gpu => Tr.T("Режим видеокарты"),
        ParameterId.Panel => Tr.T("Частота встроенного экрана"),
        ParameterId.External => Tr.T("Внешний монитор"),
        ParameterId.Charging => Tr.T("Зарядка"),
        ParameterId.Usb => Tr.T("USB во сне"),
        ParameterId.Processor => Tr.T("Процессор и схема Windows"),
        ParameterId.Timers => Tr.T("Таймеры простоя"),
        _ => Tr.T("Подсветка"),
    };

    static void ParameterErrors(string who, Profile p, int maxBrightness, List<string> errors)
    {
        var s = p.Set;
        if (s.Panel is { } panel)
        {
            if (panel.Kind == PanelRateKind.Fixed && !FirmwareRanges.PanelHz.Contains(panel.Hz))
                errors.Add(who + ": " + Tr.F("частота экрана {0} Гц не из {1}", panel.Hz, string.Join(", ", FirmwareRanges.PanelHz)));
            if (panel.Kind == PanelRateKind.Drr && panel.Hz is not (0 or 120))
                errors.Add(who + ": " + Tr.F("DRR работает только на 120 Гц, задано {0}", panel.Hz));
        }
        if (s.Processor is { Epp: { } epp } && (epp < FirmwareRanges.Epp.Min || epp > FirmwareRanges.Epp.Max))
            errors.Add(who + ": " + Tr.F("EPP {0} вне {1}..{2}", epp, FirmwareRanges.Epp.Min, FirmwareRanges.Epp.Max));
        if (s.Lighting is { } l)
        {
            if (l.Profile is < 1 or > 6) errors.Add(who + ": " + Tr.F("профиль подсветки {0} вне 1..6", l.Profile));
            if (l.Brightness is < 0 or > 9) errors.Add(who + ": " + Tr.F("яркость подсветки {0} вне 0..9", l.Brightness));
        }
        if (s.Timers is { } t) TimerErrors(who, p, t, maxBrightness, errors);
    }

    static void TimerErrors(string who, Profile p, IdleTimers t, int maxBrightness, List<string> errors)
    {
        void Minutes(string field, int v)
        {
            if (v is < 0 or > 1440) errors.Add(who + ": " + Tr.F("{0} {1} мин вне 0..1440", field, v));
        }
        Minutes(Tr.T("выключение экрана"), t.DisplayOffMin);
        Minutes(Tr.T("сон"), t.SleepMin);
        Minutes(Tr.T("гибернация"), t.HibernateMin);
        if (t.Dim is { } dim)
        {
            Minutes(Tr.T("затемнение"), dim.Minutes);
            if (dim.Brightness < 0 || dim.Brightness > maxBrightness)
                errors.Add(who + ": " + Tr.F("яркость затемнения {0} вне 0..{1}", dim.Brightness, maxBrightness));
        }
        if (t.ForcedSleepMin is { } forced)
        {
            if (forced < 1 || forced > 1440) errors.Add(who + ": " + Tr.F("сон с закрытой крышкой {0} мин вне 1..1440", forced));
            if (p.Conditions.Lid != LidState.Closed)
                errors.Add(who + ": " + Tr.T("сон с закрытой крышкой задаётся только в профиле с условием «закрыт»"));
        }

        // The stages that are not "never" rise strictly: dim < display off < sleep.
        var stages = new List<(string Name, int Min)>();
        if (t.Dim is { Minutes: > 0 } d) stages.Add((Tr.T("затемнение"), d.Minutes));
        if (t.DisplayOffMin > 0) stages.Add((Tr.T("выключение экрана"), t.DisplayOffMin));
        if (t.SleepMin > 0) stages.Add((Tr.T("сон"), t.SleepMin));
        for (var i = 1; i < stages.Count; i++)
            if (stages[i].Min <= stages[i - 1].Min)
                errors.Add(who + ": " + Tr.F("{0} ({1} мин) должно наступать раньше, чем {2} ({3} мин)", stages[i - 1].Name, stages[i - 1].Min, stages[i].Name, stages[i].Min));
    }
}
