using Microsoft.Win32;

namespace LegionModes.Core.Quick;

// SubKey is relative to Paths.DesktopMenuKey ("" = the key itself).
public sealed record RegValue(string SubKey, string Name, string Value);      // Name "" = the key's default value

public static class MenuRegistry
{
    public static IReadOnlyList<RegValue> Values(MenuItem root, string quickExe, string iconPath)
    {
        var values = new List<RegValue>
        {
            new("", "MUIVerb", root.Label),
            new("", "Icon", iconPath),
            new("", "SubCommands", ""),
        };
        Walk(root, "", quickExe, values);
        return values;
    }

    static void Walk(MenuItem parent, string parentKey, string quickExe, List<RegValue> values)
    {
        for (var i = 0; i < parent.Children.Count; i++)
        {
            var item = parent.Children[i];
            var key = Join(Join(parentKey, "shell"), $"{i + 1:00}-{item.Key}");
            values.Add(new(key, "MUIVerb", item.Label));
            if (item.Action is null)
            {
                values.Add(new(key, "SubCommands", ""));
                Walk(item, key, quickExe, values);
            }
            else
            {
                values.Add(new(Join(key, "command"), "", $"\"{quickExe}\" {QuickActions.Arg(item.Action.Value)}"));
            }
        }
    }

    static string Join(string a, string b) => a.Length == 0 ? b : a + "\\" + b;

    // HKCU. Deletes the whole menu tree, then writes every value.
    public static void Write(IReadOnlyList<RegValue> values)
    {
        Remove();
        foreach (var v in values)
        {
            using var key = Registry.CurrentUser.CreateSubKey(v.SubKey.Length == 0 ? Paths.DesktopMenuKey : Paths.DesktopMenuKey + "\\" + v.SubKey);
            key.SetValue(v.Name, v.Value, RegistryValueKind.String);
        }
    }

    public static void Remove() => Registry.CurrentUser.DeleteSubKeyTree(Paths.DesktopMenuKey, throwOnMissingSubKey: false);
}
