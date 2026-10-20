using LegionModes.Core;
using LegionModes.Core.Model;
using Xunit;

namespace LegionModes.Tests;

[Collection("Tr")]
public sealed class ProfileNamesTests : IDisposable
{
    public void Dispose() => Tr.Reset();

    static Profile Custom(string name, Dictionary<string, string>? names = null) =>
        new(Guid.NewGuid(), name, ProfileKind.Automatic, new Conditions(), 0, 0, new Parameters(), [], [], names);

    [Fact(DisplayName = "@important a profile's name: the language's own text, else English, else the name it was made with; built-in profiles have an English name")]
    public void Display()
    {
        var custom = Custom("Мой профиль");
        var both = Custom("Дом", new() { ["ru"] = "Дом", ["en"] = "Home" });
        var onlyEnglish = Custom("Work", new() { ["en"] = "Work" });
        var german = Custom("Дом", new() { ["ru"] = "Дом", ["en"] = "Home", ["de"] = "Zuhause" });
        var factory = FactoryProfiles.Create().Profiles.First(p => p.Name == "Сеть, открыт, простой");

        Assert.Equal("Мой профиль", ProfileNames.Display(custom));   // Russian before any setup
        Assert.Equal("Дом", ProfileNames.Display(both));
        Assert.Equal("Сеть, открыт, простой", ProfileNames.Display(factory));

        Tr.Use("en", Path.GetTempPath());
        Assert.Equal("Мой профиль", ProfileNames.Display(custom));   // no English text: the name it was made with
        Assert.Equal("Home", ProfileNames.Display(both));
        Assert.Equal("Work", ProfileNames.Display(onlyEnglish));
        Assert.Equal("AC, open, idle", ProfileNames.Display(factory));

        Tr.Use("de-DE", Path.GetTempPath());
        Assert.Equal("Zuhause", ProfileNames.Display(german));
        Assert.Equal("Home", ProfileNames.Display(both));            // no German text: English
        Assert.Equal("AC, open, idle", ProfileNames.Display(factory));

        Tr.Use("ru", Path.GetTempPath());
        Assert.Equal("Work", ProfileNames.Display(onlyEnglish));     // no Russian text: English
    }

    [Fact(DisplayName = "@important a name is kept under the language it was typed in; Russian also renames the profile (its preset follows)")]
    public void WithName()
    {
        var p = FactoryProfiles.Create().Profiles.First(x => x.Name == "Сеть, открыт, простой");

        Tr.Use("en", Path.GetTempPath());
        var english = ProfileNames.WithName(p, "Desk");
        Assert.Equal("Сеть, открыт, простой", english.Name);
        Assert.Equal("Desk", ProfileNames.Display(english));
        Assert.Equal("Сеть, открыт, простой", english.Names!["ru"]);

        Tr.Use("de", Path.GetTempPath());
        var german = ProfileNames.WithName(english, "Schreibtisch");
        Assert.Equal("Schreibtisch", ProfileNames.Display(german));
        Assert.Equal("Desk", german.Names!["en"]);

        Tr.Use("ru", Path.GetTempPath());
        var russian = ProfileNames.WithName(german, "Стол");
        Assert.Equal("Стол", russian.Name);
        Assert.Equal("Стол", ProfileNames.Display(russian));
        Assert.Equal("Desk", german.Names!["en"]);
    }

    [Fact(DisplayName = "@important condition-built names come in both languages")]
    public void FromConditions()
    {
        var c = new Conditions(Source: PowerSource.Battery, Lid: LidState.Closed, Load: Workload.Idle);
        Assert.Equal("Батарея, закрыт, простой", ProfileNames.FromConditions(c));
        Assert.Equal("Battery, closed, idle", ProfileNames.FromConditions(c, english: true));
        Assert.Equal("Lid closed", ProfileNames.FromConditions(new Conditions(Lid: LidState.Closed), english: true));
        Assert.Equal("Always", ProfileNames.FromConditions(new Conditions(), english: true));
    }
}
