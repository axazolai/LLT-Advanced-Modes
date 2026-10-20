using System.Text.Encodings.Web;
using System.Text.Json;

namespace LegionModes.Core.Status;

// Small JSON documents shared between processes: written to a temp file, then moved over the target.
public static class JsonFile
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // A missing or unreadable file gives null.
    public static T? Load<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options); }
        catch { return null; }
    }

    // Missing = no file; otherwise a value, or the reason it could not be read.
    public static (T? Value, bool Missing, string? Error) TryLoad<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return (null, true, null);
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            return value is null ? (null, false, "the file is empty or null") : (value, false, null);
        }
        catch (FileNotFoundException) { return (null, true, null); }
        catch (DirectoryNotFoundException) { return (null, true, null); }
        catch (Exception e) { return (null, false, e.Message); }
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
