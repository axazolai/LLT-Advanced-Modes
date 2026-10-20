namespace LegionModes.Updates;

public static class ReleaseChoice
{
    // Newer than installed only; among them the newest version the package has a plugin for, else the newest release.
    public static ReleaseInfo? Pick(IReadOnlyList<ReleaseInfo> releases, string installed, IReadOnlyCollection<string> pluginVersions)
    {
        if (!Version.TryParse(installed, out var have)) return null;
        var newer = releases
            .Select(r => (Release: r, Version: Version.TryParse(r.Version, out var v) ? v : null))
            .Where(x => x.Version is not null && x.Version > have)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Release)
            .ToList();
        return newer.FirstOrDefault(r => pluginVersions.Contains(r.Version, StringComparer.OrdinalIgnoreCase)) ?? newer.FirstOrDefault();
    }
}
