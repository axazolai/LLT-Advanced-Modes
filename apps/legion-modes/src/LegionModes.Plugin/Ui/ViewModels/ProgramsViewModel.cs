using LegionModes.Core;
using LegionModes.Core.Programs;

namespace LegionModes.Plugin.Ui.ViewModels;

// One programs.json entry as the tab edits it.
public sealed class ProgramItem
{
    public string Pattern { get; set; } = "";
    public ProgramClass Class { get; set; } = ProgramClass.Work;
    public string CommandLine { get; set; } = "";            // empty = any command line
    public bool DenyWakeRequests { get; set; }
    public bool Expanded { get; set; }

    // A full exe path without wildcards: the list shows that exe's icon.
    public string? ExePath =>
        Pattern.Trim() is var p && p.Contains('\\') && !p.Contains('*') && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : null;

    public ProgramEntry ToEntry() =>
        new(Pattern.Trim(), Class, CommandLine.Trim() is { Length: > 0 } cl ? cl : null, DenyWakeRequests);

    public static ProgramItem From(ProgramEntry e) =>
        new() { Pattern = e.Pattern, Class = e.Class, CommandLine = e.CommandLine ?? "", DenyWakeRequests = e.DenyWakeRequests };
}

// A running process: display name (file description, else exe name), exe, path, its class by the edited list (null = not
// in the list).
public sealed record ProcessItem(string Display, string Exe, string? Path, string? Description, string? CommandLine,
                                 bool Windowed, ProgramClass? Class);

public sealed class ProgramsViewModel : TabViewModel
{
    readonly ModesDocument _doc;
    List<ProcessScanner.Running> _running = [];
    string _search = "";
    bool _dirty;

    public ProgramsViewModel(ModesDocument doc)
    {
        _doc = doc;
        _doc.ProgramsChanged += Load;
        Load();
        _ = LoadProcessesAsync();
    }

    public List<ProgramItem> Items { get; } = [];
    public IReadOnlyList<ProcessItem> Processes { get; private set; } = [];
    public bool Dirty => _dirty;
    public event Action? ItemsChanged;                     // the list or its dirty state changed: the tab redraws it
    public event Action? ProcessesChanged;
    public event Action? DirtyChanged;                     // a field was typed in: only the buttons follow, the rows stay
    public event Action? IconsLoaded;                      // the entries' exe icons are in: the rows may redraw

    public static string ClassName(ProgramClass c) => c switch
    {
        ProgramClass.Heavy => Tr.T("Heavy"),
        ProgramClass.Ai => Tr.T("AI"),
        ProgramClass.Work => Tr.T("Work"),
        _ => Tr.T("Ignore"),
    };

    public static IReadOnlyList<ProgramClass> Classes { get; } = [ProgramClass.Work, ProgramClass.Ai, ProgramClass.Heavy, ProgramClass.Ignore];

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) Classify(); }
    }

    void Load()
    {
        Items.Clear();
        Items.AddRange(_doc.Programs.Entries.Select(ProgramItem.From));
        _dirty = false;
        ItemsChanged?.Invoke();
        Classify();
        _ = PrefetchIconsAsync();
    }

    // The icons of the entries' exe paths are read off the UI thread; the list redraws once they are in.
    async Task PrefetchIconsAsync()
    {
        try
        {
            var paths = Items.Select(i => i.ExePath).OfType<string>().Where(p => !ProcessIcons.TryCached(p, out _)).ToList();
            if (paths.Count == 0) return;
            await Task.Run(() => { foreach (var p in paths) ProcessIcons.Of(p); });
            IconsLoaded?.Invoke();
        }
        catch (Exception e) { Log.Error("program icons", e); }
    }

    // A field of an item changed while it is being typed in.
    public void Touched()
    {
        _dirty = true;
        DirtyChanged?.Invoke();
        Classify();
    }

    // Any edit of an item, an add or a delete: the processes are re-marked by the edited list.
    public void Edited()
    {
        _dirty = true;
        ItemsChanged?.Invoke();
        Classify();
        _ = PrefetchIconsAsync();
    }

    public void Add(ProgramItem item)
    {
        Items.Add(item);
        Info("");
        Edited();
    }

    public void Remove(ProgramItem item)
    {
        Items.Remove(item);
        Edited();
    }

    // A running process into the list: its full path, else its exe name.
    public void AddProcess(ProcessItem p, ProgramClass c)
    {
        var pattern = p.Path ?? p.Exe;
        var existing = Items.FirstOrDefault(i => string.Equals(i.Pattern.Trim(), pattern, StringComparison.OrdinalIgnoreCase) && i.CommandLine.Trim().Length == 0);
        if (existing is not null) existing.Class = c;
        else Items.Add(new ProgramItem { Pattern = pattern, Class = c });
        Info(Tr.F("\"{0}\" — {1}; remember to save", p.Display, ClassName(c).ToLowerInvariant()));
        Edited();
    }

    public void Revert()
    {
        Load();
        Info("");
    }

    ProgramsConfig Build() => new(ProgramsConfig.CurrentSchema, Items.Select(i => i.ToEntry()).ToArray());

    public Task SaveAsync() => GuardAsync(Tr.T("Could not save the program list"), async () =>
    {
        var config = Build();
        var errors = ProgramsStore.Errors(config);
        if (errors.Count > 0) { IsError = true; Message = string.Join("; ", errors); return; }
        var warn = await _doc.SaveProgramsAsync(config);
        Saved(Tr.T("Program list saved"), warn);
    });

    // Enumerated off the UI thread: path, command line, window, file description, icon.
    public async Task LoadProcessesAsync()
    {
        try
        {
            _running = await Task.Run(() =>
            {
                var list = ProcessScanner.Snapshot();
                foreach (var p in list) { ProcessIcons.Description(p.Path); ProcessIcons.Of(p.Path); }
                return list;
            });
            Classify();
        }
        catch (Exception e) { Fail(Tr.T("Could not get the process list"), e); }
    }

    // Windowed ones first, then the rest; each alphabetically by display name; one row per exe path (or name).
    void Classify()
    {
        List<ProgramEntry> entries;
        try { entries = Items.Where(i => i.Pattern.Trim().Length > 0).Select(i => i.ToEntry()).ToList(); }
        catch (Exception e) { Log.Error("programs classify", e); return; }
        var classifier = new ProgramClassifier(entries);
        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Processes = _running
            .GroupBy(r => r.Path ?? r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var r = g.OrderByDescending(x => x.Windowed).First();
                var exe = r.Path is { } path ? System.IO.Path.GetFileName(path) : r.Name + ".exe";
                var description = ProcessIcons.Description(r.Path);
                ProgramClass? cls = null;
                foreach (var x in g)
                    if (classifier.Classify(x.Path, x.Name, x.CommandLine) is { } c && (cls is null || c > cls)) cls = c;
                return new ProcessItem(description ?? exe, exe, r.Path, description, r.CommandLine, g.Any(x => x.Windowed), cls);
            })
            .Where(p => words.All(w => Contains(p.Display, w) || Contains(p.Exe, w) || Contains(p.Path, w) || Contains(p.Description, w)))
            .OrderByDescending(p => p.Windowed)
            .ThenBy(p => p.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ProcessesChanged?.Invoke();
    }

    static bool Contains(string? text, string word) => text?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true;
}
