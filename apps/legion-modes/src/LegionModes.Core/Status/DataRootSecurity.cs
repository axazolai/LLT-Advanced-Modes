using System.Security.AccessControl;
using System.Security.Principal;

namespace LegionModes.Core.Status;

// The data root holds installers the elevated keeper runs, so only SYSTEM and Administrators may write there.
public static class DataRootSecurity
{
    // Creates the folder when missing. DACL: SYSTEM and Administrators FullControl, Users ReadAndExecute,
    // inherited by children, protected (no inherited ACEs).
    public static void Apply(string path)
    {
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    // True when `path` resolves to a location strictly below `root` (no "..", no sibling with a common prefix).
    public static bool IsUnder(string root, string path)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path);
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) && p.Length > r.Length;
    }
}
