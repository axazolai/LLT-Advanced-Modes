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
        TestLang.Russian();
        foreach (var d in Directory.GetDirectories(_dir)) { foreach (var f in Directory.GetFiles(d)) File.Delete(f); Directory.Delete(d); }
        foreach (var f in Directory.GetFiles(_dir)) File.Delete(f);
        Directory.Delete(_dir);
    }

    void Write(string name, string json) => File.WriteAllText(Path.Combine(_dir, name + ".json"), json);

    [Fact(DisplayName = "@important Tr: the English key shows unless the language's file has a text; the language part lies under the full code; a context suffix is never shown")]
    public void Lookup()
    {
        Write("ru", """{"Yes":"Да","Found {0}":"Найдено {0}","Update":"Обновить","Update|2":"Обновление"}""");
        Write("de", """{"Yes":"Ja"}""");
        Write("de-AT", """{"Yes":"Jawohl"}""");

        Tr.Reset();
        Assert.Equal("Yes", Tr.T("Yes"));
        Assert.Equal("Update", Tr.T("Update|2"));

        Tr.Use("ru-RU", _dir);
        Assert.Equal("Да", Tr.T("Yes"));
        Assert.Equal("Найдено 3", Tr.F("Found {0}", 3));
        Assert.Equal("Обновление", Tr.T("Update|2"));
        Assert.Equal("No", Tr.T("No"));            // a key without a text stays English

        Tr.Use("uk", _dir);
        Assert.Equal("Yes", Tr.T("Yes"));          // no file for the language: English
        Assert.Equal("Found 3", Tr.F("Found {0}", 3));

        Tr.Use("de-AT", _dir);
        Assert.Equal("Jawohl", Tr.T("Yes"));
        Tr.Use("de-DE", _dir);
        Assert.Equal("Ja", Tr.T("Yes"));
        Tr.Use("", _dir);
        Assert.Equal("Ja", Tr.T("Yes"));
    }

    [Fact(DisplayName = "@important Tr: the owner's folder is laid over the package's, so an update never removes an added translation")]
    public void OwnerFolderWins()
    {
        var user = Path.Combine(_dir, "user");
        Directory.CreateDirectory(user);
        Write("de", """{"Yes":"Ja","No":"Nein"}""");
        File.WriteAllText(Path.Combine(user, "de.json"), """{"Yes":"Jo"}""");
        Tr.Use("de", _dir, user);
        Assert.Equal("Jo", Tr.T("Yes"));
        Assert.Equal("Nein", Tr.T("No"));
    }

    [Fact(DisplayName = "@important Tr: the language comes from LLT's lang file; a broken translation file is skipped")]
    public void FromFile()
    {
        Write("de", "not json");
        Write("ru", """{"Yes":"Да"}""");
        var lang = Path.Combine(_dir, "lang");
        File.WriteAllText(lang, "de");
        Tr.FromLlt(lang, _dir);
        Assert.Equal("Yes", Tr.T("Yes"));
        File.WriteAllText(lang, "ru");
        Tr.FromLlt(lang, _dir);
        Assert.Equal("Да", Tr.T("Yes"));
    }
}
