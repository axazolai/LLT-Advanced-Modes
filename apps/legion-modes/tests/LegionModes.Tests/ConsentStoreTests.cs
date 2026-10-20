using LegionModes.Core.Status;
using Microsoft.Win32;

namespace LegionModes.Tests;

public sealed class ConsentStoreTests : IDisposable
{
    readonly string _root = @"Software\LegionModes.Tests\ConsentStore." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false); } catch { }
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\LegionModes.Tests");
            if (parent is not null && parent.SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(@"Software\LegionModes.Tests", throwOnMissingSubKey: false);
        }
        catch { }
    }

    void App(string path, long start, long stop)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_root + "\\" + path);
        key.SetValue("LastUsedTimeStart", start, RegistryValueKind.QWord);
        key.SetValue("LastUsedTimeStop", stop, RegistryValueKind.QWord);
    }

    [Fact(DisplayName = "@important ConsentStore lists the apps using the device: open packaged and desktop apps only")]
    public void ListsAppsInUse()
    {
        App("Microsoft.WindowsCamera_8wekyb3d8bbwe", 133_000_000_000, 0);       // using now
        App("Some.Finished_abc", 133_000_000_000, 133_000_100_000);              // stopped
        App("Never.Used_abc", 0, 0);                                             // never used
        App(@"NonPackaged\C:#Program Files#Foo#foo.exe", 133_000_000_000, 0);   // desktop app, using now
        App(@"NonPackaged\C:#Program Files#Bar#bar.exe", 133_000_000_000, 133_000_100_000);

        using var root = Registry.CurrentUser.OpenSubKey(_root)!;
        var inUse = ConsentStore.InUse(root);

        Assert.Equal(2, inUse.Count);
        Assert.Contains("Microsoft.WindowsCamera_8wekyb3d8bbwe", inUse);
        Assert.Contains("foo.exe", inUse);
        Assert.DoesNotContain("bar.exe", inUse);
        Assert.DoesNotContain("Some.Finished_abc", inUse);
    }
}
