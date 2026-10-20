using LegionModes.Core.Ui;

namespace LegionModes.Tests;

public class EditorLayoutTests
{
    static string Temp() => Path.Combine(Path.GetTempPath(), "lm-layout-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact(DisplayName = "@important Editor layout: a saved layout comes back; unknown and repeated keys go, missing keys return to their default column")]
    public void NormalizeAndRoundTrip()
    {
        var moved = new EditorLayout([["Power", EditorLayout.Profile], ["Timers", "Gpu", "Gpu", "Bogus"], []]);
        var n = EditorLayout.Normalize(moved);
        Assert.Equal(["Power", EditorLayout.Profile, EditorLayout.Overlaps, EditorLayout.Actions], n.Columns[0]);
        Assert.Equal(["Timers", "Gpu", "Charging", "Usb", "Lighting"], n.Columns[1]);
        Assert.Equal(["Processor", "Panel", "External"], n.Columns[2]);
        Assert.Equal(EditorLayout.Keys.Order(), n.Columns.SelectMany(c => c).Order());

        var path = Temp();
        try
        {
            EditorLayout.Save(path, n);
            Assert.Equal(n.Columns, EditorLayout.Load(path).Columns);
        }
        finally { File.Delete(path); }
    }

    [Fact(DisplayName = "@important Editor layout: a missing, unreadable or two-column file gives the owner's default")]
    public void FallsBackToDefault()
    {
        var path = Temp();
        Assert.Equal(EditorLayout.Default.Columns, EditorLayout.Load(path).Columns);
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Equal(EditorLayout.Default.Columns, EditorLayout.Load(path).Columns);
            File.WriteAllText(path, "[[\"Power\"],[\"Gpu\"]]");
            Assert.Equal(EditorLayout.Default.Columns, EditorLayout.Load(path).Columns);
        }
        finally { File.Delete(path); }
        Assert.Equal([EditorLayout.Profile, EditorLayout.Overlaps, "Timers", EditorLayout.Actions], EditorLayout.Default.Columns[0]);
        Assert.Equal(["Power", "Processor", "Gpu", "Panel", "External"], EditorLayout.Default.Columns[2]);
    }
}
