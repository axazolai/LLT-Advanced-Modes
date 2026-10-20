using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LegionModes.Updates;

namespace LegionModes.Tests;

public sealed class ReconcilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lm-recon-" + Guid.NewGuid().ToString("N"));
    private string Backup => Path.Combine(_root, "backup");
    private string Live => Path.Combine(_root, "live");

    public ReconcilerTests()
    {
        Directory.CreateDirectory(Backup);
        Directory.CreateDirectory(Live);
    }

    public void Dispose()
    {
        foreach (var d in new[] { Backup, Live })
        {
            foreach (var f in Directory.GetFiles(d)) File.Delete(f);
            Directory.Delete(d);
        }
        Directory.Delete(_root);
    }

    private void Put(string dir, string file, string json) => File.WriteAllText(Path.Combine(dir, file), json);
    private JsonNode Read(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(Live, file)))!;

    private static string Godmode(string lmValue, string otherValue) =>
        """
        {"ActivePresetId":"1","Presets":{
          "1":{"Name":"LM · ac.open.heavy","CPULongTermPowerLimit":{"Value":@LM,"Min":5,"Max":140}},
          "2":{"Name":"Game Mode","CPULongTermPowerLimit":{"Value":@OTHER,"Min":5,"Max":140}}}}
        """.Replace("@LM", lmValue).Replace("@OTHER", otherValue);

    [Fact(DisplayName = "@critical settings.json MinimizeToTray false in live is restored to true and listed")]
    public void RestoresChangedBool()
    {
        Put(Backup, "settings.json", """{"MinimizeToTray":true,"Other":1}""");
        Put(Live, "settings.json", """{"MinimizeToTray":false,"Other":1}""");
        var r = SettingsReconciler.Reconcile(Backup, Live, []);
        Assert.True((bool)Read("settings.json")["MinimizeToTray"]!);
        var c = Assert.Single(r.Restored);
        Assert.Equal(("settings.json", "$.MinimizeToTray"), (c.File, c.JsonPath));
    }

    [Fact(DisplayName = "@important a new live field is kept and listed in Added; a backup-only field is Removed and not written")]
    public void AddedKeptRemovedNotWritten()
    {
        Put(Backup, "settings.json", """{"Old":1,"Same":2}""");
        Put(Live, "settings.json", """{"New":3,"Same":2}""");
        var r = SettingsReconciler.Reconcile(Backup, Live, []);
        Assert.Equal("$.New", Assert.Single(r.Added).JsonPath);
        Assert.Equal("$.Old", Assert.Single(r.Removed).JsonPath);
        var live = Read("settings.json");
        Assert.Equal(3, (int)live["New"]!);
        Assert.Null(live["Old"]);
        Assert.Empty(r.Restored);
    }

    [Fact(DisplayName = "@important a field that changed kind is not restored and is listed as a type change")]
    public void TypeChangeNotRestored()
    {
        Put(Backup, "settings.json", """{"Level":5}""");
        Put(Live, "settings.json", """{"Level":"five"}""");
        var r = SettingsReconciler.Reconcile(Backup, Live, []);
        Assert.Equal("five", (string)Read("settings.json")["Level"]!);
        Assert.Empty(r.Restored);
        var c = Assert.Single(r.Removed);
        Assert.Equal(("$.Level", "5", "\"five\""), (c.JsonPath, c.Before, c.After));
    }

    [Fact(DisplayName = "@critical update_settings.json PerDay is restored to Never and the key holds")]
    public void UpdateFrequencyRestored()
    {
        Put(Backup, "update_settings.json", """{"UpdateCheckFrequency":"Never"}""");
        Put(Live, "update_settings.json", """{"UpdateCheckFrequency":"PerDay"}""");
        var r = SettingsReconciler.Reconcile(Backup, Live, KeySettings.For(false));
        Assert.Equal("Never", (string)Read("update_settings.json")["UpdateCheckFrequency"]!);
        Assert.Single(r.Restored);
        Assert.DoesNotContain(r.KeyFailures, f => f.StartsWith("update_settings.json"));
    }

    [Fact(DisplayName = "@critical update_settings.json PerDay with a PerDay backup is a key failure and Ok is false")]
    public void UpdateFrequencyUnrestorableFails()
    {
        Put(Backup, "update_settings.json", """{"UpdateCheckFrequency":"PerDay"}""");
        Put(Live, "update_settings.json", """{"UpdateCheckFrequency":"PerDay"}""");
        var r = SettingsReconciler.Reconcile(Backup, Live, KeySettings.For(false));
        Assert.False(r.Ok);
        Assert.Contains(r.KeyFailures, f => f.Contains("update_settings.json") && f.Contains("UpdateCheckFrequency"));
    }

    [Fact(DisplayName = "@critical a changed LM preset is restored; a changed non-LM preset is restored as an ordinary field, never a key failure")]
    public void PresetRestoreAndKey()
    {
        Put(Backup, "godmode.json", Godmode("100", "60"));
        Put(Live, "godmode.json", Godmode("40", "20"));
        var r = SettingsReconciler.Reconcile(Backup, Live, KeySettings.For(false));
        var live = Read("godmode.json");
        Assert.Equal(100, (int)live["Presets"]!["1"]!["CPULongTermPowerLimit"]!["Value"]!);
        Assert.Equal(60, (int)live["Presets"]!["2"]!["CPULongTermPowerLimit"]!["Value"]!);
        Assert.Equal(2, r.Restored.Count);
        Assert.DoesNotContain(r.KeyFailures, f => f.StartsWith("godmode.json"));
    }

    [Fact(DisplayName = "@critical extra live fields in an LM preset, also nested ones, keep the key holding")]
    public void PresetKeySubsetAllowsExtraFields()
    {
        var keys = KeySettings.For(false).Where(k => k.File == "godmode.json").ToList();
        Put(Backup, "godmode.json", Godmode("100", "60"));
        Put(Live, "godmode.json", new Regex("\"Max\":140").Replace(Godmode("100", "60"), "\"Max\":140,\"New\":1", 1)
            .Replace("\"Name\":\"LM · ac.open.heavy\"", "\"Name\":\"LM · ac.open.heavy\",\"Extra\":2"));
        var r = SettingsReconciler.Reconcile(Backup, Live, keys);
        Assert.True(r.Ok);
        Assert.Equal(2, r.Added.Count);
    }

    [Fact(DisplayName = "@critical an LM preset field of another kind in live is a key failure")]
    public void PresetKeyFailsOnKindChange()
    {
        var keys = KeySettings.For(false).Where(k => k.File == "godmode.json").ToList();
        Put(Backup, "godmode.json", Godmode("100", "60"));
        Put(Live, "godmode.json", Godmode("\"100\"", "60"));
        var r = SettingsReconciler.Reconcile(Backup, Live, keys);
        Assert.False(r.Ok);
        Assert.Contains(r.KeyFailures, f => f.StartsWith("godmode.json"));
    }

    [Fact(DisplayName = "@critical an LM preset field the new LLT dropped (CPUPL1Tau) is listed in Removed, not a key failure")]
    public void PresetKeyToleratesDroppedField()
    {
        var keys = KeySettings.For(false).Where(k => k.File == "godmode.json").ToList();
        Put(Backup, "godmode.json", Godmode("100", "60"));
        Put(Live, "godmode.json", Godmode("100", "60").Replace(",\"Min\":5,\"Max\":140", ""));
        var r = SettingsReconciler.Reconcile(Backup, Live, keys);
        Assert.True(r.Ok);
        Assert.Contains(r.Removed, c => c.JsonPath.EndsWith(".Min"));
    }

    [Fact(DisplayName = "@critical a non-LM preset with an unrepairable difference is never a key failure")]
    public void NonLmPresetNeverKeyFailure()
    {
        var keys = KeySettings.For(false).Where(k => k.File == "godmode.json").ToList();
        Put(Backup, "godmode.json", Godmode("100", "60"));
        Put(Live, "godmode.json", Godmode("100", "\"60\""));
        var r = SettingsReconciler.Reconcile(Backup, Live, keys);
        Assert.True(r.Ok);
        Assert.Contains(r.Removed, c => c.JsonPath.Contains("Value"));
    }

    [Fact(DisplayName = "@important a changed $type makes the whole object a type change and nothing inside is restored")]
    public void TypeDiscriminatorChange()
    {
        Put(Backup, "automation.json", """{"Steps":{"S":{"$type":"A.One","X":1,"Y":2}}}""");
        Put(Live, "automation.json", """{"Steps":{"S":{"$type":"A.Two","X":9,"Y":2}}}""");
        var r = SettingsReconciler.Reconcile(Backup, Live, []);
        Assert.Empty(r.Restored);
        var c = Assert.Single(r.Removed);
        Assert.Equal("$.Steps.S", c.JsonPath);
        Assert.Contains("A.One", c.Before);
        Assert.Contains("A.Two", c.After);
        Assert.Equal(9, (int)Read("automation.json")["Steps"]!["S"]!["X"]!);
    }

    [Fact(DisplayName = "@important a missing live file whose backup does not parse is skipped and reported")]
    public void MissingLiveUnparseableBackup()
    {
        Put(Backup, "x.json", "{ broken");
        var r = SettingsReconciler.Reconcile(Backup, Live, []);
        Assert.False(File.Exists(Path.Combine(Live, "x.json")));
        Assert.Contains(r.Removed, c => c.File == "x.json" && c.JsonPath == "$");
    }

    [Fact(DisplayName = "@important a live file that cannot be read is an error, not an unparseable file")]
    public void LiveReadErrorRethrown()
    {
        Put(Backup, "a.json", """{"A":1}""");
        Put(Live, "a.json", """{"A":2}""");
        using var _ = new FileStream(Path.Combine(Live, "a.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Throws<IOException>(() => SettingsReconciler.Reconcile(Backup, Live, []));
    }

    [Theory(DisplayName = "@critical hasControl true restores the control-owned settings; hasControl false does not make them key settings")]
    [InlineData("automation.json", "IsEnabled", "false", "true")]
    [InlineData("settings.json", "PowerModeMappingMode", "\"Disabled\"", "\"WindowsPowerPlan\"")]
    public void ControlKeys(string file, string field, string backupValue, string liveValue)
    {
        Put(Backup, file, $"{{\"{field}\":{backupValue}}}");
        Put(Live, file, $"{{\"{field}\":{liveValue}}}");
        var r = SettingsReconciler.Reconcile(Backup, Live, KeySettings.For(true));
        Assert.Equal(backupValue, Read(file)[field]!.ToJsonString());
        Assert.Single(r.Restored);
        Assert.DoesNotContain(r.KeyFailures, f => f.StartsWith(file));

        // Backup holds the bad value too: with control it fails, without control it is not a key setting.
        Put(Backup, file, $"{{\"{field}\":{liveValue}}}");
        Put(Live, file, $"{{\"{field}\":{liveValue}}}");
        Assert.Contains(SettingsReconciler.Reconcile(Backup, Live, KeySettings.For(true)).KeyFailures, f => f.StartsWith(file));
        Assert.DoesNotContain(SettingsReconciler.Reconcile(Backup, Live, KeySettings.For(false)).KeyFailures, f => f.StartsWith(file));
    }

    [Fact(DisplayName = "@important a file missing in live is copied back from the backup")]
    public void MissingFileCopiedBack()
    {
        Put(Backup, "settings.json", """{"A":1}""");
        var r = SettingsReconciler.Reconcile(Backup, Live, []);
        Assert.Equal(1, (int)Read("settings.json")["A"]!);
        Assert.Equal("settings.json", Assert.Single(r.Restored).File);
    }

    [Fact(DisplayName = "@important an unparseable live file is replaced by the backup; an unparseable backup leaves live alone and is reported")]
    public void UnparseableFiles()
    {
        Put(Backup, "a.json", """{"A":1}""");
        Put(Live, "a.json", "{ not json");
        Put(Backup, "b.json", "{ broken");
        Put(Live, "b.json", """{"B":2}""");
        var r = SettingsReconciler.Reconcile(Backup, Live, []);
        Assert.Equal(1, (int)Read("a.json")["A"]!);
        Assert.Contains(r.Restored, c => c.File == "a.json" && c.JsonPath == "$");
        Assert.Equal(2, (int)Read("b.json")["B"]!);
        Assert.Contains(r.Removed, c => c.File == "b.json" && c.JsonPath == "$");
    }

    [Fact(DisplayName = "@important restored files keep property order, are indented, UTF-8 without BOM, and arrays are restored whole")]
    public void WriteFormat()
    {
        Put(Backup, "settings.json", """{"Z":1,"List":[1,2,3],"Name":"LM · x","A":true}""");
        Put(Live, "settings.json", """{"Z":1,"List":[1,2],"Name":"LM · x","A":false}""");
        var r = SettingsReconciler.Reconcile(Backup, Live, []);
        Assert.Equal(2, r.Restored.Count);
        var bytes = File.ReadAllBytes(Path.Combine(Live, "settings.json"));
        Assert.NotEqual(0xEF, bytes[0]);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\n", text);
        Assert.Contains("LM · x", text);
        Assert.True(text.IndexOf("\"Z\"") < text.IndexOf("\"List\"") && text.IndexOf("\"List\"") < text.IndexOf("\"A\""));
        Assert.Equal(3, ((JsonArray)Read("settings.json")["List"]!).Count);
        Assert.Empty(Directory.GetFiles(Live, "*.lm-tmp"));
    }
}
