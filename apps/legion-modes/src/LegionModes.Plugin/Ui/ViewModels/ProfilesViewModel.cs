using LegionModes.Core;
using LegionModes.Core.Model;

namespace LegionModes.Plugin.Ui.ViewModels;

// The ordered profile list. The root stays first and «Автономия» last; automatic profiles move among themselves.
// Every change is a whole new ModesConfig saved through the document (validated by the runtime before it is written).
public sealed class ProfilesViewModel : TabViewModel
{
    readonly ModesDocument _doc;
    readonly IModesHost _host;
    readonly IDialogs _dialogs;

    public ProfilesViewModel(ModesDocument doc, IModesHost host, IDialogs dialogs)
    {
        _doc = doc;
        _host = host;
        _dialogs = dialogs;
        doc.ModesChanged += () => Raise(nameof(Config));
    }

    public event Action? ListChanged;

    public ModesConfig Config => _doc.Modes;
    public Guid? InForce => _host.Last?.Profile.Id;
    public int MaxBrightness => _host.MaxBrightness;
    public IModesHost Host => _host;
    public ModesDocument Document => _doc;

    public bool CanMove(Profile p, int step)
    {
        if (p.Kind != ProfileKind.Automatic) return false;
        var target = Config.IndexOf(p) + step;
        return target >= 0 && target < Config.Profiles.Count && Config.Profiles[target].Kind == ProfileKind.Automatic;
    }

    public Task MoveAsync(Profile p, int step) => GuardAsync(Tr.T("Не удалось переместить профиль"), async () =>
    {
        if (!CanMove(p, step)) return;
        var list = Config.Profiles.ToList();
        var i = Config.IndexOf(p);
        (list[i], list[i + step]) = (list[i + step], list[i]);
        await SaveAsync(Config with { Profiles = list }, Tr.T("Порядок сохранён"));
    });

    // Drag and drop: the profile goes to list index `to` (counted in the list without it), kept after the root and
    // before «Автономия».
    public Task MoveToAsync(Profile p, int to) => GuardAsync(Tr.T("Не удалось переместить профиль"), async () =>
    {
        if (p.Kind != ProfileKind.Automatic) return;
        var list = Config.Profiles.Where(x => x.Id != p.Id).ToList();
        var manual = list.FindIndex(x => x.Kind == ProfileKind.Manual) is var m and >= 0 ? m : list.Count;
        to = Math.Clamp(to, 1, manual);
        if (to == Config.IndexOf(p)) return;
        list.Insert(to, p);
        await SaveAsync(Config with { Profiles = list }, Tr.T("Порядок сохранён"));
    });

    public Task RenameAsync(Profile p) => GuardAsync(Tr.T("Не удалось переименовать профиль"), async () =>
    {
        if (p.Kind != ProfileKind.Automatic) return;
        var name = (await _dialogs.PromptAsync(Tr.T("Переименовать профиль"), Tr.T("Название"), ProfileNames.Display(p)))?.Trim();
        if (string.IsNullOrEmpty(name) || name == ProfileNames.Display(p)) return;
        await SaveAsync(Replace(p, ProfileNames.WithName(p, name)), Tr.T("Профиль переименован"));
    });

    public Task DeleteAsync(Profile p) => GuardAsync(Tr.T("Не удалось удалить профиль"), async () =>
    {
        if (p.Kind != ProfileKind.Automatic) return;
        if (!await _dialogs.ConfirmAsync(Tr.F("Удалить профиль «{0}»?", ProfileNames.Display(p)),
                Tr.T("Его параметры и действия пропадут; профили, которые наследовали от него, возьмут значения выше по цепочке."),
                Tr.T("Удалить"))) return;
        await SaveAsync(Config with { Profiles = Config.Profiles.Where(x => x.Id != p.Id).ToList() }, Tr.T("Профиль удалён"));
    });

    // A new automatic profile (sets nothing, inherits everything) goes to list index `at` (after the root, at most just
    // before «Автономия»), or before «Автономия» when `at` is null.
    public Task AddAsync(Conditions conditions, int? at = null) => GuardAsync(Tr.T("Не удалось добавить профиль"), async () =>
    {
        var (name, names) = NewName(conditions);
        var profile = new Profile(Guid.NewGuid(), name, ProfileKind.Automatic, conditions, 0, 0, new Parameters(), [], [], names);
        var list = Config.Profiles.ToList();
        var manual = list.FindIndex(x => x.Kind == ProfileKind.Manual) is var m and >= 0 ? m : list.Count;
        list.Insert(Math.Clamp(at ?? manual, 1, manual), profile);
        await SaveAsync(Config with { Profiles = list }, Tr.F("Профиль «{0}» добавлен", name));
    });

    // A copy takes new conditions (two automatic profiles never share them) and goes right below the original.
    public Task CopyAsync(Profile p, Conditions conditions) => GuardAsync(Tr.T("Не удалось скопировать профиль"), async () =>
    {
        if (p.Kind != ProfileKind.Automatic) return;
        var ids = new Dictionary<Guid, Guid>();
        var pipelines = p.Pipelines.Select(x => { var id = Guid.NewGuid(); ids[x.Id] = id; return x with { Id = id, Llt = WithId(x.Llt, x.Id, id) }; }).ToList();
        var order = p.Order.Select(s => ids.TryGetValue(s.Id, out var id) ? s with { Id = id } : s).ToList();
        var (copyName, copyNames) = NewName(conditions);
        var copy = p with
        {
            Id = Guid.NewGuid(), Name = copyName, Names = copyNames, Conditions = conditions,
            Pipelines = pipelines, Order = order,
        };
        var list = Config.Profiles.ToList();
        list.Insert(Config.IndexOf(p) + 1, copy);
        await SaveAsync(Config with { Profiles = list }, Tr.F("Копия «{0}» добавлена", ProfileNames.Display(copy)));
    });

    // The automatic profile that already holds this combination, if any.
    public Profile? Holder(Conditions c, Guid? except = null) =>
        Config.Profiles.FirstOrDefault(x => x.Kind == ProfileKind.Automatic && x.Id != except && x.Conditions == c);

    public async Task SaveAsync(ModesConfig config, string ok)
    {
        var error = await TrySaveAsync(config, ok);
        if (error is not null) { IsError = true; Message = error; }
    }

    // null when saved; else the reason, for the editor to show.
    public async Task<string?> TrySaveAsync(ModesConfig config, string ok)
    {
        try
        {
            var warn = await _doc.SaveModesAsync(config);
            Saved(ok, warn);
            return null;
        }
        catch (ConfigInvalidException e)
        {
            return Tr.F("Не сохранено: {0}", string.Join("; ", e.Errors));
        }
        finally { ListChanged?.Invoke(); }
    }

    ModesConfig Replace(Profile old, Profile updated) =>
        Config with { Profiles = Config.Profiles.Select(x => x.Id == old.Id ? updated : x).ToList() };

    // The name of a new profile in the current language, and the names to keep with it.
    (string Name, Dictionary<string, string> Names) NewName(Conditions conditions)
    {
        var key = ProfileNames.LanguageKey();
        var name = UniqueName(ProfileNames.FromConditions(conditions, english: key != "ru"));
        return (name, new Dictionary<string, string> { [key == "ru" ? "ru" : "en"] = name });
    }

    string UniqueName(string name)
    {
        if (Config.Profiles.All(x => x.Name != name)) return name;
        for (var n = 2; ; n++)
            if (Config.Profiles.All(x => x.Name != $"{name} ({n})")) return $"{name} ({n})";
    }

    // The pipeline JSON carries its own id; a copied pipeline gets the new one.
    static string WithId(string llt, Guid old, Guid id) => llt.Replace(old.ToString(), id.ToString(), StringComparison.OrdinalIgnoreCase);
}
