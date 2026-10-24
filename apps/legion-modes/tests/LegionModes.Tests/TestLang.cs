using System.Runtime.CompilerServices;
using LegionModes.Core;

namespace LegionModes.Tests;

// Most tests assert the Russian texts: the interface language of the test run is Russian, read from the plugin's lang folder.
internal static class TestLang
{
    public static string LangDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "LegionModes.Core"))) dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("apps/legion-modes not found"), "src", "LegionModes.Plugin", "lang");
        }
    }

    [ModuleInitializer]
    public static void Init() => Russian();

    public static void Russian() => Tr.Use("ru", LangDir);
}
