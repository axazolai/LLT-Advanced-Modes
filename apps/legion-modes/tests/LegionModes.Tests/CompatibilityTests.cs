using LegionModes.Core.Contracts;
using LegionModes.Updates;

namespace LegionModes.Tests;

public class CompatibilityTests
{
    private static readonly string[] Host = ["FakeHost"];

    private static string Fixtures
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "LegionModes.sln"))) d = d.Parent;
            return Path.Combine(d!.FullName, "tests", "fixtures");
        }
    }

    private static string Client => Path.Combine(Fixtures, "FakeClient", "bin", "out", "FakeClient.dll");
    private static string StableClient => Path.Combine(Fixtures, "FakeClientStable", "bin", "out", "FakeClientStable.dll");
    private static string V1 => Path.Combine(Fixtures, "FakeHost.V1", "bin", "V1");
    private static string V2 => Path.Combine(Fixtures, "FakeHost.V2", "bin", "V2");

    private static CompatibilityReport Check(string plugin, string hostDir, params ReflectionContract[] contracts) =>
        CompatibilityChecker.Check(plugin, hostDir, Host, contracts);

    [Fact(DisplayName = "@critical client against its own host version is compatible and counts references")]
    public void SameVersionIsCompatible()
    {
        var r = Check(Client, V1);
        Assert.True(r.Compatible, string.Join("; ", r.Problems));
        Assert.True(r.ReferencesChecked > 0);
    }

    [Fact(DisplayName = "@critical a removed method is reported with type and member")]
    public void RemovedMethodIsReported()
    {
        var r = Check(Client, V2);
        Assert.False(r.Compatible);
        Assert.Contains(r.Problems, p => p.Contains("FakeHost.Widget.Removable"));
    }

    [Fact(DisplayName = "@critical a method with a new parameter or another return type is reported")]
    public void ChangedSignatureIsReported()
    {
        var r = Check(Client, V2);
        Assert.False(r.Compatible);
        Assert.Contains(r.Problems, p => p.Contains("FakeHost.Widget.AddsParam"));
        Assert.Contains(r.Problems, p => p.Contains("FakeHost.Widget.ChangesReturn"));
        Assert.DoesNotContain(r.Problems, p => p.Contains("Echo") || p.Contains(".Count") || p.Contains("Box"));
    }

    [Fact(DisplayName = "@critical a plugin type missing a member that a host interface gained is reported")]
    public void GrownHostInterfaceIsReported()
    {
        var r = Check(Client, V2);
        Assert.Contains(r.Problems, p => p.Contains("FakeClient.Impl") && p.Contains("IProvider.Extra"));
    }

    [Fact(DisplayName = "@critical a changed abstract base member and a lost overridable base member are reported")]
    public void ChangedAbstractBaseIsReported()
    {
        var r = Check(Client, V2);
        Assert.Contains(r.Problems, p => p.Contains("FakeClient.Derived") && p.Contains("Compute"));
        Assert.Contains(r.Problems, p => p.Contains("FakeClient.Derived.Describe") && p.Contains("overrides nothing"));
    }

    [Fact(DisplayName = "@critical a concrete type inheriting an interface through a plugin base is checked")]
    public void InterfaceOfAbstractPluginBaseIsChecked()
    {
        var r = Check(Client, V2);
        Assert.Contains(r.Problems, p => p.Contains("FakeClient.PConcrete") && p.Contains("Extra"));
    }

    [Fact(DisplayName = "@critical an implementation inherited from a host base class satisfies the interface")]
    public void HostBaseImplementationCounts()
    {
        var r = Check(Client, V2);
        Assert.DoesNotContain(r.Problems, p => p.Contains("FakeClient.FromHost"));
    }

    [Fact(DisplayName = "@critical an override of a member the host now seals is reported")]
    public void SealedHostOverrideIsReported()
    {
        var r = Check(Client, V2);
        Assert.Contains(r.Problems, p => p.Contains("FakeClient.Tuned.Tune") && p.Contains("sealed"));
        Assert.DoesNotContain(Check(Client, V1).Problems, p => p.Contains("Tune"));
    }

    [Fact(DisplayName = "@critical an unchanged in parameter matches, in changed to by-value is reported")]
    public void InParameterIsCompared()
    {
        var r = Check(Client, V2);
        Assert.Contains(r.Problems, p => p.Contains("Widget.ToValue"));
        Assert.DoesNotContain(r.Problems, p => p.Contains("UseIn"));
    }

    [Fact(DisplayName = "@critical an inherited protected host member that was removed is reported")]
    public void RemovedInheritedProtectedMemberIsReported()
    {
        var r = Check(Client, V2);
        Assert.Contains(r.Problems, p => p.Contains("BaseThing.Gone"));
        Assert.DoesNotContain(r.Problems, p => p.Contains("Helper"));
    }

    [Fact(DisplayName = "@critical a used member that became internal is reported, a protected one stays usable")]
    public void InaccessibleMemberIsReported()
    {
        var r = Check(Client, V2);
        Assert.Contains(r.Problems, p => p.Contains("Widget.Hidden"));
        Assert.DoesNotContain(r.Problems, p => p.Contains("Helper"));
    }

    [Fact(DisplayName = "@critical a setter that became init-only is reported")]
    public void InitOnlySetterIsReported()
    {
        var r = Check(Client, V2);
        Assert.Contains(r.Problems, p => p.Contains("Widget.set_Tag"));
    }

    [Fact(DisplayName = "@critical a static field that became an instance field is reported")]
    public void StaticnessOfFieldIsReported()
    {
        var r = Check(Client, V2);
        Assert.Contains(r.Problems, p => p.Contains("Widget.Shared"));
    }

    [Fact(DisplayName = "@important a miscased host assembly name still checks the references")]
    public void HostNamesAreCaseInsensitive()
    {
        var r = CompatibilityChecker.Check(Client, V1, ["fakehost"], []);
        Assert.True(r.Compatible, string.Join("; ", r.Problems));
        Assert.True(r.ReferencesChecked > 0);
    }

    [Fact(DisplayName = "@important the host set is the referenced assemblies present in the host directory")]
    public void HostAssembliesOfListsReferencedHostFiles()
    {
        var set = CompatibilityChecker.HostAssembliesOf(Client, V1);
        Assert.Contains("FakeHost", set);
        Assert.DoesNotContain("System.Runtime", set);
        Assert.Empty(CompatibilityChecker.HostAssembliesOf(Client, Directory.GetCurrentDirectory() + "\\none"));
    }

    [Fact(DisplayName = "@important a changed unused member and an added member stay compatible")]
    public void UnusedChangesAreIgnored()
    {
        var r = Check(StableClient, V2);
        Assert.True(r.Compatible, string.Join("; ", r.Problems));
        Assert.True(r.ReferencesChecked > 0);
    }

    [Fact(DisplayName = "@critical a reflection contract on a renamed field is reported")]
    public void RenamedContractFieldIsReported()
    {
        var present = new ReflectionContract("FakeHost", "FakeHost.Widget", "_state", MemberKind.Field, "System.Int32");
        Assert.True(Check(StableClient, V1, present).Compatible);
        var r = Check(StableClient, V2, present);
        Assert.False(r.Compatible);
        Assert.Contains(r.Problems, p => p.Contains("_state"));
    }

    [Fact(DisplayName = "@critical a reflection contract on a field of another type is reported")]
    public void RetypedContractFieldIsReported()
    {
        var c = new ReflectionContract("FakeHost", "FakeHost.Widget", "_label", MemberKind.Field, "System.String");
        Assert.True(Check(StableClient, V1, c).Compatible);
        var r = Check(StableClient, V2, c);
        Assert.False(r.Compatible);
        Assert.Contains(r.Problems, p => p.Contains("_label"));
    }

    [Fact(DisplayName = "@important a missing host assembly file is a problem naming the file, not an exception")]
    public void MissingHostFileIsAProblem()
    {
        var empty = Directory.CreateTempSubdirectory("lm-host-").FullName;
        try
        {
            var r = Check(Client, empty);
            Assert.False(r.Compatible);
            Assert.Contains(r.Problems, p => p.Contains("FakeHost.dll"));
        }
        finally { Directory.Delete(empty); }
    }

    [Fact(DisplayName = "@important a host file that is not an assembly is a problem, not an exception")]
    public void BadHostImageIsAProblem()
    {
        var dir = Directory.CreateTempSubdirectory("lm-host-").FullName;
        var bad = Path.Combine(dir, "FakeHost.dll");
        File.WriteAllText(bad, "not an assembly");
        try
        {
            var r = Check(Client, dir);
            Assert.False(r.Compatible);
            Assert.Contains(r.Problems, p => p.Contains("FakeHost.dll"));
        }
        finally { File.Delete(bad); Directory.Delete(dir); }
    }

    [Fact(DisplayName = "@important highlight marks GodMode and fan lines and leaves the others")]
    public void HighlightMarksKeywordLines()
    {
        var body = "## 2.34\r\n- Fixed GodMode preset crash\r\n- Better fan curve editor\n- Updated translations\n\n- Typo";
        var lines = ChangelogHighlighter.Highlight(body);
        Assert.Equal(6, lines.Count);
        Assert.Equal("- Fixed GodMode preset crash", lines[1].Line);
        Assert.True(lines[1].Highlight);
        Assert.True(lines[2].Highlight);
        Assert.False(lines[0].Highlight);
        Assert.False(lines[3].Highlight);
        Assert.False(lines[4].Highlight);
        Assert.False(lines[5].Highlight);
    }

    [Fact(DisplayName = "@important a plugin type deriving from a WPF type resolves through the WindowsDesktop assemblies")]
    public void WpfBaseTypeResolves()
    {
        var shared = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('\\', '/')).Parent!.Parent!;
        var desktop = Directory.GetDirectories(Path.Combine(shared.FullName, "Microsoft.WindowsDesktop.App"))
            .OrderByDescending(d => d).First();
        var client = Path.Combine(Fixtures, "FakeWpfClient", "bin", "out", "FakeWpfClient.dll");
        var r = CompatibilityChecker.Check(client, desktop, CompatibilityChecker.HostAssembliesOf(client, desktop), []);
        Assert.True(r.Compatible, string.Join("; ", r.Problems));
        Assert.True(r.ReferencesChecked > 0);
    }
}
