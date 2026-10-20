namespace LegionModes.Core;

public static class Paths
{
    public const string PluginId = "LegionModes";
    public const string PresetPrefix = "AM · ";          // "Advanced modes": the label of the plugin's GodMode presets
    public const string LegacyPresetPrefix = "LM · ";    // the label before 2026-10-20; such presets are renamed to the new one

    public static bool IsOurs(string? presetName) =>
        presetName is not null && (presetName.StartsWith(PresetPrefix, StringComparison.Ordinal) || presetName.StartsWith(LegacyPresetPrefix, StringComparison.Ordinal));

    // The text after the label (either label), or the whole name when it carries none.
    public static string PresetText(string presetName) =>
        presetName.StartsWith(PresetPrefix, StringComparison.Ordinal) ? presetName[PresetPrefix.Length..]
        : presetName.StartsWith(LegacyPresetPrefix, StringComparison.Ordinal) ? presetName[LegacyPresetPrefix.Length..] : presetName;
    public const string KeeperTask = "LegionModes Keeper";
    public const string GuardService = "LegionModesGuard";
    public const string GuardPipe = "LegionModes.Guard";
    public const string KeeperPipe = "LegionModes.Keeper";
    public const string PluginPipe = "LegionModes.Plugin";
    public const string QuickPipe = "LegionModes.Quick";
    public const string LltExe = @"C:\Program Files\LenovoLegionToolkit\Lenovo Legion Toolkit.exe";
    public const string InstallDir = @"C:\Program Files\LegionModes";
    public const string QuickExe = @"C:\Program Files\LegionModes\LegionModes.Quick.exe";
    public const string DesktopMenuKey = @"Software\Classes\DesktopBackground\Shell\LegionModes";   // under HKCU

    public static string LltDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LenovoLegionToolkit");

    public static string PluginDll { get; } = Path.Combine(LltDataDir, "Plugins", PluginId, PluginId + ".dll");

#if DEBUG
    // Smoke runs only: Release builds never read the variable (the elevated keeper's environment is user-writable).
    public static bool DataRootOverridden => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEGIONMODES_DATA_ROOT"));
#else
    public static bool DataRootOverridden => false;
#endif

    public static string DataRoot()
    {
#if DEBUG
        var overridden = Environment.GetEnvironmentVariable("LEGIONMODES_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
#endif
        return Directory.Exists(@"D:\")
            ? @"D:\ProgramData\LegionModes"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionModes");
    }
}
