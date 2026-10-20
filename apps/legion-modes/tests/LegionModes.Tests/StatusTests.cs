using LegionModes.Core.Status;

namespace LegionModes.Tests;

public sealed class StatusTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "LegionModes.Status." + Guid.NewGuid().ToString("N"));

    public StatusTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact(DisplayName = "@important updates.json round-trips the last check, the schedule and the history")]
    public void UpdatesFileRoundTrips()
    {
        var path = Path.Combine(_dir, "sub", "updates.json");
        var at = new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(3));
        var original = new UpdatesFile(at, at.AddHours(24), "2.34.0.0",
            new CheckSummary(new ReleaseSummary("2.36.5.17", "v2.36.5.17", at, "GodMode fix\nother"), false,
                ["Lenovo Legion Toolkit: missing member X"], 41,
                [new ChangelogLine("GodMode fix", true), new ChangelogLine("other", false)], null),
            [new HistoryEntry(at, "2.36.5.17", "v2.36.5.17", false, "Compat", "missing member X", "")]);

        original.Save(path);
        var loaded = UpdatesFile.Load(path)!;

        Assert.Equal(original.LastCheck, loaded.LastCheck);
        Assert.Equal(original.NextCheck, loaded.NextCheck);
        Assert.Equal("2.34.0.0", loaded.InstalledVersion);
        Assert.Equal(original.Last!.Release, loaded.Last!.Release);
        Assert.False(loaded.Last.Compatible);
        Assert.Equal(original.Last.Problems, loaded.Last.Problems);
        Assert.Equal(41, loaded.Last.ReferencesChecked);
        Assert.Equal(original.Last.Changelog, loaded.Last.Changelog);
        Assert.Equal(original.History, loaded.History);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact(DisplayName = "@important updates.json that is missing or corrupt loads as null")]
    public void UpdatesFileMissingOrCorrupt()
    {
        var path = Path.Combine(_dir, "updates.json");
        Assert.Null(UpdatesFile.Load(path));
        File.WriteAllText(path, "{ not json");
        Assert.Null(UpdatesFile.Load(path));
    }

    [Fact(DisplayName = "@critical a path counts as under the data root only when it resolves strictly below it")]
    public void IsUnderRejectsEscapes()
    {
        var root = Path.Combine(_dir, "root");
        Assert.True(DataRootSecurity.IsUnder(root, Path.Combine(root, "installers", "1.0", "setup.exe")));
        Assert.True(DataRootSecurity.IsUnder(root + @"\", Path.Combine(root, "a.exe")));
        Assert.False(DataRootSecurity.IsUnder(root, root));
        Assert.False(DataRootSecurity.IsUnder(root, root + "-evil" + @"\setup.exe"));
        Assert.False(DataRootSecurity.IsUnder(root, Path.Combine(root, "..", "evil", "setup.exe")));
        Assert.False(DataRootSecurity.IsUnder(root, @"C:\Users\Public\setup.exe"));
    }

    [Fact(DisplayName = "@critical the data root DACL is protected: SYSTEM and Administrators FullControl, Users ReadAndExecute, inherited, nothing else")]
    public void DataRootAclIsProtected()
    {
        var root = Path.Combine(_dir, "acl-root");
        try
        {
            DataRootSecurity.Apply(root);
            var sec = new DirectoryInfo(root).GetAccessControl();
            Assert.True(sec.AreAccessRulesProtected);
            var rules = sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
            Assert.Equal(3, rules.Count);
            Assert.All(rules, r =>
            {
                Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, r.AccessControlType);
                Assert.False(r.IsInherited);
                Assert.Equal(System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit, r.InheritanceFlags);
            });
            Dictionary<System.Security.Principal.WellKnownSidType, System.Security.AccessControl.FileSystemRights> expected = new()
            {
                [System.Security.Principal.WellKnownSidType.LocalSystemSid] = System.Security.AccessControl.FileSystemRights.FullControl,
                [System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid] = System.Security.AccessControl.FileSystemRights.FullControl,
                [System.Security.Principal.WellKnownSidType.BuiltinUsersSid] = System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            };
            foreach (var (kind, rights) in expected)
            {
                var sid = new System.Security.Principal.SecurityIdentifier(kind, null);
                var actual = rules.Single(r => r.IdentityReference.Equals(sid)).FileSystemRights & ~System.Security.AccessControl.FileSystemRights.Synchronize;
                if (rights == System.Security.AccessControl.FileSystemRights.FullControl)   // reported without the Delete bit on a container
                {
                    const System.Security.AccessControl.FileSystemRights full = System.Security.AccessControl.FileSystemRights.Modify
                        | System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership;
                    Assert.Equal(full, actual & full);
                }
                else Assert.Equal(rights, actual);
            }
        }
        finally
        {
            // The owner can always rewrite the DACL: give the test user back full control so Dispose can delete the folder.
            try
            {
                var back = new DirectoryInfo(root).GetAccessControl();
                back.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                new DirectoryInfo(root).SetAccessControl(back);
            }
            catch { }
        }
    }

    [Fact(DisplayName = "@important pending notifications: Append twice, TakeAll returns both in order and empties the file")]
    public void PendingNotificationsAppendThenTake()
    {
        var path = Path.Combine(_dir, "notifications-pending.json");
        PendingNotifications.Append(path, "first");
        PendingNotifications.Append(path, "second");

        Assert.Equal(["first", "second"], PendingNotifications.TakeAll(path));
        Assert.Empty(PendingNotifications.TakeAll(path));
        Assert.Equal("[]", File.ReadAllText(path).Trim());
    }
}
