using LegionModes.Core.Model;
using LegionModes.Core.Programs;

namespace LegionModes.Tests;

public sealed class ProgramsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "legionmodes-" + Guid.NewGuid().ToString("N"));
    public ProgramsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    static ProgramEntry W(string p) => new(p, ProgramClass.Work);
    static ProgramEntry H(string p) => new(p, ProgramClass.Heavy);

    [Fact(DisplayName = "@important glob: ** matches any depth, case-insensitive; * stays within one segment")]
    public void Glob()
    {
        Assert.True(PathPattern.IsMatch(@"D:\1__ProgramFiles\JetBrains\**", @"d:\1__programfiles\jetbrains\Rider\bin\rider64.exe"));
        Assert.True(PathPattern.IsMatch(@"D:\Games\*\game.exe", @"D:\Games\a\game.exe"));
        Assert.False(PathPattern.IsMatch(@"D:\Games\*\game.exe", @"D:\Games\a\b\game.exe"));
    }

    [Fact(DisplayName = "@important bare exe name matches in any folder, by process name without a path; path entries need a path")]
    public void BareName()
    {
        var c = new ProgramClassifier([W("rider64.exe"), H(@"D:\Games\**")]);
        Assert.Equal(ProgramClass.Work, c.Classify(@"C:\x\y\RIDER64.EXE", "rider64", null));
        Assert.Equal(ProgramClass.Work, c.Classify(null, "rider64", null));
        Assert.Equal(ProgramClass.Work, c.Classify(null, "Rider64.exe", null));
        Assert.Null(new ProgramClassifier([H(@"D:\Games\**")]).Classify(null, "game", null));
    }

    [Fact(DisplayName = "@important forward slashes, trailing backslash and %VAR% match; folder prefix is not a name prefix")]
    public void Normalization()
    {
        var pf = Environment.ExpandEnvironmentVariables("%ProgramFiles%");
        Assert.True(PathPattern.IsMatch("%ProgramFiles%/Foo/", Path.Combine(pf, "Foo", "x.exe")));
        Assert.True(PathPattern.IsMatch(@"C:\Program Files\Foo\**", @"C:\Program Files\Foo\x.exe"));
        Assert.True(PathPattern.IsMatch(@"C:\Program Files\Foo\", @"C:\Program Files\Foo\x.exe"));
        Assert.False(PathPattern.IsMatch(@"C:\Program Files\Foo\", @"C:\Program Files\FooBar\x.exe"));
    }

    [Fact(DisplayName = "@important command-line entry matches a matching command line only, never an unknown one")]
    public void CommandLineEntry()
    {
        var c = new ProgramClassifier([new ProgramEntry("ollama.exe", ProgramClass.Ai, "*serve*")]);
        Assert.Equal(ProgramClass.Ai, c.Classify(@"C:\x\ollama.exe", "ollama", "\"C:\\x\\ollama.exe\" serve"));
        Assert.Null(c.Classify(@"C:\x\ollama.exe", "ollama", "\"C:\\x\\ollama.exe\" run llama3"));
        Assert.Null(c.Classify(@"C:\x\ollama.exe", "ollama", null));
        Assert.True(CommandLinePattern.IsMatch("*SERVE*", "ollama Serve --x"));
    }

    [Fact(DisplayName = "@important highest class wins: Heavy > Ai > Work > Ignore; only Ignore -> Ignore; no entry -> null")]
    public void HighestClassWins()
    {
        ProgramEntry A(string p) => new(p, ProgramClass.Ai);
        ProgramEntry I(string p) => new(p, ProgramClass.Ignore);
        Assert.Equal(ProgramClass.Heavy, new ProgramClassifier([W("g.exe"), A("g.exe"), H("g.exe")]).Classify(null, "g", null));
        Assert.Equal(ProgramClass.Ai, new ProgramClassifier([W("g.exe"), A("g.exe")]).Classify(null, "g", null));
        Assert.Equal(ProgramClass.Work, new ProgramClassifier([I("g.exe"), W("g.exe")]).Classify(null, "g", null));
        Assert.Equal(ProgramClass.Ignore, new ProgramClassifier([I("g.exe")]).Classify(null, "g", null));
        Assert.Null(new ProgramClassifier([W("g.exe")]).Classify(null, "h", null));
    }

    [Fact(DisplayName = "@important tracker with a zero delay: AI at once, Heavy over it, stop Heavy -> Ai at once")]
    public void TrackerZeroDelay()
    {
        var t = new WorkloadTracker(new FakeClock(), TimeSpan.Zero);
        Assert.Equal(Workload.Idle, t.Current);
        t.Started(1, ProgramClass.Ai); Assert.Equal(Workload.Ai, t.Current);
        t.Started(2, ProgramClass.Heavy); Assert.Equal(Workload.Heavy, t.Current);
        t.Stopped(2); Assert.Equal(Workload.Ai, t.Current);
        t.Started(3, ProgramClass.Ignore); t.Stopped(1); Assert.Equal(Workload.Idle, t.Current);
    }

    [Fact(DisplayName = "@critical ProgramsStore.Load on a schema-1 file keeps every entry and reports a Notice; invalid JSON -> empty, error, file unchanged")]
    public void LoadSchemas()
    {
        var v1 = Path.Combine(_dir, "v1.json");
        File.WriteAllText(v1, "{\"schemaVersion\":1,\"rules\":[{\"class\":\"Work\",\"pattern\":\"rider64.exe\"},{\"class\":\"Heavy\",\"pattern\":\"D:\\\\Games\\\\**\"}]}");
        var r = ProgramsStore.Load(v1);
        Assert.Null(r.Error); Assert.NotNull(r.Notice);
        Assert.Equal([new ProgramEntry("rider64.exe", ProgramClass.Work), new ProgramEntry(@"D:\Games\**", ProgramClass.Heavy)], r.Value.Entries);
        Assert.Equal(ProgramsConfig.CurrentSchema, r.Value.SchemaVersion);

        var path = Path.Combine(_dir, "programs.json");
        File.WriteAllText(path, "{ not json");
        var bad = ProgramsStore.Load(path);
        Assert.NotNull(bad.Error); Assert.Empty(bad.Value.Entries);
        Assert.Equal("{ not json", File.ReadAllText(path));
        Assert.Null(ProgramsStore.Load(Path.Combine(_dir, "none.json")).Error);

        var ok = Path.Combine(_dir, "ok.json");
        var cfg = new ProgramsConfig(2, [new ProgramEntry("vlc.exe", ProgramClass.Ignore, "*x*", true), H("g.exe")]);
        ProgramsStore.Save(ok, cfg);
        var back = ProgramsStore.Load(ok);
        Assert.Null(back.Error); Assert.Null(back.Notice);
        Assert.Equal(cfg.Entries, back.Value.Entries);
    }

    [Fact(DisplayName = "@important wake flag is refused on folder and wildcard patterns; ExeNames gives the exe file name")]
    public void WakeFlag()
    {
        ProgramsConfig C(string p) => new(2, [new ProgramEntry(p, ProgramClass.Work, null, true)]);
        Assert.NotEmpty(ProgramsStore.Errors(C(@"D:\Games\**")));
        Assert.NotEmpty(ProgramsStore.Errors(C("*.exe")));
        Assert.Empty(ProgramsStore.Errors(C("vlc.exe")));
        Assert.Equal(["vlc.exe"], WakeOverrides.ExeNames(C(@"C:\Program Files\VideoLAN\VLC\vlc.exe")));
        Assert.Equal(["vlc.exe"], WakeOverrides.ExeNames(C("vlc.exe")));
        Assert.Empty(WakeOverrides.ExeNames(new(2, [W("vlc.exe")])));
    }

    [Fact(DisplayName = "@important WakeRequests.Parse returns the requests with their details and nothing for empty sections")]
    public void ParseRequests()
    {
        const string sample = "DISPLAY:\r\n[PROCESS] \\Device\\HarddiskVolume3\\Program Files\\VideoLAN\\VLC\\vlc.exe\r\nVideo Wake Lock\r\n\r\n"
            + "SYSTEM:\r\n[DRIVER] Realtek High Definition Audio (HDAUDIO\\FUNC_01&VEN_10EC&DEV_0287&SUBSYS_17AA3C26&REV_1000\\4&2b5c3e8&0&0001)\r\nAn audio stream is currently in use.\r\n\r\n"
            + "AWAYMODE:\r\nNone.\r\n\r\nEXECUTION:\r\nNone.\r\n";
        Assert.Equal(
        [
            new WakeRequest("DISPLAY", "PROCESS", "vlc.exe", "Video Wake Lock"),
            new WakeRequest("SYSTEM", "DRIVER", "Realtek High Definition Audio", "An audio stream is currently in use."),
        ], WakeRequests.Parse(sample));
        Assert.Empty(WakeRequests.Parse("DISPLAY:\nНет.\n\nSYSTEM:\nНет.\n"));
    }
}
