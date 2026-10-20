using Microsoft.Win32;

namespace LegionModes.Core.Status;

// Windows' per-app capability log: HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\<capability>.
// A packaged app is a subkey named after the package; a desktop app is a child of "NonPackaged" named after its exe path
// with every '\' written as '#'. An app is using the device while LastUsedTimeStart is set and LastUsedTimeStop is 0.
public static class ConsentStore
{
    public const string Microphone = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    public const string Webcam = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    const string NonPackaged = "NonPackaged";

    // Names of the apps using the device now: the package key name, or the exe file name of a desktop app.
    public static IReadOnlyList<string> InUse(RegistryKey capabilityRoot)
    {
        var result = new List<string>();
        foreach (var name in capabilityRoot.GetSubKeyNames())
        {
            using var key = capabilityRoot.OpenSubKey(name);
            if (key is null) continue;
            if (string.Equals(name, NonPackaged, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(child);
                    if (app is not null && IsInUse(app)) result.Add(ExeName(child));
                }
            }
            else if (IsInUse(key)) result.Add(name);
        }
        return result;
    }

    // The capability key under HKCU; null when the machine has no such key.
    public static IReadOnlyList<string> InUse(string capabilityPath)
    {
        using var root = Registry.CurrentUser.OpenSubKey(capabilityPath);
        return root is null ? [] : InUse(root);
    }

    static bool IsInUse(RegistryKey app) => ReadTicks(app, "LastUsedTimeStart") != 0 && ReadTicks(app, "LastUsedTimeStop") == 0;

    static long ReadTicks(RegistryKey key, string value) => key.GetValue(value) switch
    {
        long l => l,
        int i => i,
        _ => 0,
    };

    static string ExeName(string escapedPath)
    {
        var path = escapedPath.Replace('#', '\\');
        var i = path.LastIndexOf('\\');
        return i < 0 ? path : path[(i + 1)..];
    }
}
