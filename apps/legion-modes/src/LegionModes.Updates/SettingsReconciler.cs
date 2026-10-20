using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LegionModes.Updates;

public sealed record FieldChange(string File, string JsonPath, string? Before, string? After);

public sealed record ReconcileResult(
    IReadOnlyList<FieldChange> Restored, IReadOnlyList<FieldChange> Added,
    IReadOnlyList<FieldChange> Removed, IReadOnlyList<string> KeyFailures)
{
    public bool Ok => KeyFailures.Count == 0;
}

public static class SettingsReconciler
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ReconcileResult Reconcile(string backupDir, string liveDir, IReadOnlyList<KeySetting> keys)
    {
        var restored = new List<FieldChange>();
        var added = new List<FieldChange>();
        var removed = new List<FieldChange>();

        foreach (var bFile in Directory.GetFiles(backupDir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(bFile);
            var lFile = Path.Combine(liveDir, name);
            var bNode = TryParse(bFile, out var bOk);
            if (!bOk)
            {
                removed.Add(new FieldChange(name, "$", "<unparseable>", null));
                continue;
            }
            if (!File.Exists(lFile))
            {
                WriteBytes(lFile, File.ReadAllBytes(bFile));
                restored.Add(new FieldChange(name, "$", null, "<backup copy>"));
                continue;
            }
            var lNode = TryParse(lFile, out var lOk);
            if (!lOk)
            {
                WriteBytes(lFile, File.ReadAllBytes(bFile));
                restored.Add(new FieldChange(name, "$", "<unparseable>", "<backup copy>"));
                continue;
            }

            var changed = false;
            var lRoot = Wrap(lNode);
            Diff(name, "$", Wrap(bNode), lRoot, "v", restored, added, removed, ref changed);
            if (changed) WriteBytes(lFile, JsonSerializer.SerializeToUtf8Bytes(lRoot["v"], Indented));
        }

        var failures = new List<string>();
        foreach (var k in keys)
        {
            bool holds;
            try
            {
                var b = Navigate(TryParseIfExists(Path.Combine(backupDir, k.File)), k.JsonPath);
                var r = Navigate(TryParseIfExists(Path.Combine(liveDir, k.File)), k.JsonPath);
                holds = k.Holds(b, r);
            }
            catch { holds = false; }
            if (!holds) failures.Add($"{k.File} {k.JsonPath}: {k.Describe}");
        }
        return new ReconcileResult(restored, added, removed, failures);
    }

    // A one-slot holder lets the root itself be replaced like any other member.
    private static JsonObject Wrap(JsonNode? n) => new() { ["v"] = n };

    private static void Diff(string file, string path, JsonObject bParent, JsonObject lParent, string key,
        List<FieldChange> restored, List<FieldChange> added, List<FieldChange> removed, ref bool changed)
    {
        var b = bParent[key];
        var l = lParent[key];
        if (b is JsonObject bo && l is JsonObject lo)
        {
            if (bo["$type"] is JsonValue bt && lo["$type"] is JsonValue lt && !JsonNode.DeepEquals(bt, lt))
            {
                removed.Add(new FieldChange(file, path, Text(b), Text(l)));
                return;
            }
            foreach (var kv in bo.ToList())
            {
                var p = $"{path}.{kv.Key}";
                if (!lo.ContainsKey(kv.Key)) removed.Add(new FieldChange(file, p, Text(kv.Value), null));
                else Diff(file, p, bo, lo, kv.Key, restored, added, removed, ref changed);
            }
            foreach (var kv in lo)
                if (!bo.ContainsKey(kv.Key)) added.Add(new FieldChange(file, $"{path}.{kv.Key}", null, Text(kv.Value)));
            return;
        }
        if (JsonNode.DeepEquals(b, l)) return;
        if (Kind(b) != Kind(l))
        {
            removed.Add(new FieldChange(file, path, Text(b), Text(l)));
            return;
        }
        restored.Add(new FieldChange(file, path, Text(l), Text(b)));
        lParent[key] = b?.DeepClone();
        changed = true;
    }

    private static string Kind(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True or JsonValueKind.False => "bool",
            var k => k.ToString(),
        },
        _ => "other",
    };

    private static string? Text(JsonNode? n) => n?.ToJsonString();

    private static JsonNode? TryParse(string file, out bool ok)
    {
        try { var n = JsonNode.Parse(File.ReadAllText(file)); ok = true; return n; }
        catch (Exception e) when (e is JsonException or ArgumentException) { ok = false; return null; }
    }

    private static JsonNode? TryParseIfExists(string file) => File.Exists(file) ? TryParse(file, out _) : null;

    private static JsonNode? Navigate(JsonNode? root, string path)
    {
        var node = root;
        foreach (var part in path.Split('.').Skip(1))
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(part, out node)) return null;
        }
        return node;
    }

    private static void WriteBytes(string target, byte[] bytes)
    {
        var tmp = target + ".lm-tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(target)) File.Replace(tmp, target, null);
        else File.Move(tmp, target);
    }
}
