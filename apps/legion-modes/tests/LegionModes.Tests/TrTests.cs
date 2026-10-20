using LegionModes.Core;
using Xunit;

namespace LegionModes.Tests;

[Collection("Tr")]
public sealed class TrTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "legionmodes-tr-" + Guid.NewGuid().ToString("N"));

    public TrTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Tr.Reset();
        foreach (var f in Directory.GetFiles(_dir)) File.Delete(f);
        Directory.Delete(_dir);
    }

    void Write(string name, string json) => File.WriteAllText(Path.Combine(_dir, name + ".json"), json);

    [Fact(DisplayName = "@important Tr: Russian keeps the source text; any other language takes en.json; a language file wins; a missing key stays Russian")]
    public void Lookup()
    {
        Write("en", """{"да":"yes","Найдено {0}":"Found {0}"}""");
        Write("de", """{"да":"ja"}""");
        Assert.Equal("да", Tr.T("да"));
        Tr.Use("ru-RU", _dir);
        Assert.Equal("да", Tr.T("да"));
        Tr.Use("uk", _dir);
        Assert.Equal("yes", Tr.T("да"));
        Assert.Equal("Found 3", Tr.F("Найдено {0}", 3));
        Assert.Equal("нет", Tr.T("нет"));
        Tr.Use("de-DE", _dir);
        Assert.Equal("ja", Tr.T("да"));
        Assert.Equal("Found 3", Tr.F("Найдено {0}", 3));   // a key the German file lacks comes from en
        Tr.Use("", _dir);
        Assert.Equal("ja", Tr.T("да"));
    }

    [Fact(DisplayName = "@important Tr: the owner's folder is laid over the package's, so an update never removes an added translation")]
    public void OwnerFolderWins()
    {
        var user = Path.Combine(_dir, "user");
        Directory.CreateDirectory(user);
        try
        {
            Write("en", """{"да":"yes","нет":"no"}""");
            File.WriteAllText(Path.Combine(user, "en.json"), """{"да":"yeah"}""");
            Tr.Use("en", _dir, user);
            Assert.Equal("yeah", Tr.T("да"));
            Assert.Equal("no", Tr.T("нет"));
        }
        finally { foreach (var f in Directory.GetFiles(user)) File.Delete(f); Directory.Delete(user); }
    }

    [Fact(DisplayName = "@important Tr: the language comes from LLT's lang file; a broken translation file is skipped for the next one")]
    public void FromFile()
    {
        Write("en", """{"да":"yes"}""");
        Write("de", "not json");
        var lang = Path.Combine(_dir, "lang");
        File.WriteAllText(lang, "de");
        Tr.FromLlt(lang, _dir);
        Assert.Equal("yes", Tr.T("да"));
        File.WriteAllText(lang, "ru");
        Tr.FromLlt(lang, _dir);
        Assert.Equal("да", Tr.T("да"));
    }
}
