namespace LegionModes.Keeper;

internal static class FileMirror
{
    // Makes dst a mirror of src: copies new and changed files, deletes files and folders absent from src.
    // excludeTop names one top-level folder that is neither copied nor deleted (LLT's log folder).
    public static void Mirror(string src, string dst, string? excludeTop = null) => MirrorDir(src, dst, excludeTop);

    static void MirrorDir(string src, string dst, string? excludeTop)
    {
        Directory.CreateDirectory(dst);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(src))
        {
            var name = Path.GetFileName(file);
            fileNames.Add(name);
            var target = Path.Combine(dst, name);
            if (!Same(file, target))
            {
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                File.Copy(file, target, overwrite: true);
            }
        }
        var dirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (Excluded(name, excludeTop)) continue;
            dirNames.Add(name);
            MirrorDir(dir, Path.Combine(dst, name), null);
        }
        foreach (var file in Directory.GetFiles(dst))
        {
            if (fileNames.Contains(Path.GetFileName(file))) continue;
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        foreach (var dir in Directory.GetDirectories(dst))
        {
            var name = Path.GetFileName(dir);
            if (dirNames.Contains(name) || Excluded(name, excludeTop)) continue;
            Directory.Delete(dir, recursive: true);
        }
    }

    static bool Excluded(string name, string? excludeTop) =>
        excludeTop is not null && string.Equals(name, excludeTop, StringComparison.OrdinalIgnoreCase);

    static bool Same(string a, string b)
    {
        if (!File.Exists(b)) return false;
        var x = new FileInfo(a);
        var y = new FileInfo(b);
        return x.Length == y.Length && x.LastWriteTimeUtc == y.LastWriteTimeUtc;
    }
}
