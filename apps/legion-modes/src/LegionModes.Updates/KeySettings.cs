using System.Text.Json.Nodes;
using LegionModes.Core;

namespace LegionModes.Updates;

/// <summary>A setting whose loss after an update triggers a rollback. Holds(backupNode, resultNode) gets the nodes at JsonPath.</summary>
public sealed record KeySetting(string File, string JsonPath, Func<JsonNode?, JsonNode?, bool> Holds, string Describe);

public static class KeySettings
{
    public static IReadOnlyList<KeySetting> For(bool hasControl)
    {
        var list = new List<KeySetting>
        {
            new("update_settings.json", "$.UpdateCheckFrequency", (_, r) => IsString(r, "Never"), Tr.T("UpdateCheckFrequency must be Never (LLT update check is off)")),
            new("integrations.json", "$.CLI", (_, r) => IsBool(r, true), Tr.T("CLI must be enabled (true)")),
        };
        if (hasControl)
        {
            list.Add(new("automation.json", "$.IsEnabled", (_, r) => IsBool(r, false), Tr.T("LLT automation must be off (IsEnabled = false)")));
            list.Add(new("settings.json", "$.PowerModeMappingMode", (_, r) => IsString(r, "Disabled"), Tr.T("PowerModeMappingMode must be Disabled")));
        }
        list.Add(new("godmode.json", "$.Presets", PresetsHold, Tr.F("every \"{0}\" preset must keep all fields from the backup that exist in the new LLT version, with the same values", Paths.PresetPrefix)));
        return list;
    }

    private static bool IsString(JsonNode? n, string expected) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && s == expected;

    private static bool IsBool(JsonNode? n, bool expected) =>
        n is JsonValue v && v.TryGetValue<bool>(out var b) && b == expected;

    private static bool PresetsHold(JsonNode? backup, JsonNode? result)
    {
        if (backup is not JsonObject b) return true;
        var live = (result as JsonObject)?.Select(kv => kv.Value as JsonObject).Where(o => o is not null).ToList() ?? [];
        foreach (var bp in b.Select(kv => kv.Value as JsonObject))
        {
            var name = NameOf(bp);
            if (name is null || !Paths.IsOurs(name)) continue;
            if (!live.Any(lp => NameOf(lp) == name && Subset(bp, lp))) return false;
        }
        return true;
    }

    // Every backup field the live node still has is equal in it, recursing into objects; extra live fields are allowed,
    // and a field the live node lacks is a schema removal (the reconciler lists it in Removed), not a failure.
    private static bool Subset(JsonNode? b, JsonNode? l)
    {
        if (b is JsonObject bo)
            return l is JsonObject lo && bo.All(kv => !lo.TryGetPropertyValue(kv.Key, out var lv) || Subset(kv.Value, lv));
        return JsonNode.DeepEquals(b, l);
    }

    private static string? NameOf(JsonObject? o) =>
        o?["Name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
