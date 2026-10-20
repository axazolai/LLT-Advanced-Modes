using System.Text.Json;
using LegionModes.Core.Contracts;
using LegionModes.Updates;

namespace LegionModes.Tests;

public class PinnedLltTests
{
    [Fact(DisplayName = "@important the built plugin is compatible with the LLT release pinned in llt.lock.json")]
    public void BuiltPluginIsCompatibleWithPinnedLlt()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LegionModes.sln"))) d = d.Parent;
        var app = d!.FullName;
        var root = Path.GetFullPath(Path.Combine(app, "..", ".."));
        using var lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "llt.lock.json")));
        var version = lockFile.RootElement.GetProperty("llt").GetProperty("version").GetString()!;
        var host = Path.Combine(root, "_work", "llt-ref", version, "{app}");
        Assert.True(Directory.Exists(host), host);
        var plugin = new[] { "Release", "Debug" }
            .Select(c => Path.Combine(app, "src", "LegionModes.Plugin", "bin", "x64", c, "net9.0-windows10.0.26100.0", "LegionModes.dll"))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).First();
        var r = CompatibilityChecker.Check(plugin, host, CompatibilityChecker.HostAssembliesOf(plugin, host), ReflectionContracts.All);
        Assert.True(r.Compatible, string.Join("; ", r.Problems));
        Assert.Empty(r.Problems);
        Assert.True(r.ReferencesChecked > 0);
    }
}
