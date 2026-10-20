using System.Security.AccessControl;
using System.Security.Principal;

namespace LegionModes.Core.Status;

// notifications-pending.json: keeper and guard append, the plugin takes. One named mutex guards the file.
public static class PendingNotifications
{
    const string MutexName = @"Global\LegionModes.Notifications";
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    public static void Append(string path, string text)
    {
        using var _ = Lock();
        var list = Read(path);
        list.Add(text);
        JsonFile.Save(path, list);
    }

    public static IReadOnlyList<string> TakeAll(string path)
    {
        using var _ = Lock();
        var list = Read(path);
        if (list.Count > 0) JsonFile.Save(path, new List<string>());
        return list;
    }

    static List<string> Read(string path) => JsonFile.Load<List<string>>(path) ?? [];

    static IDisposable Lock()
    {
        var security = new MutexSecurity();
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(sid, null), MutexRights.FullControl, AccessControlType.Allow));
        var mutex = MutexAcl.Create(false, MutexName, out _, security);
        try
        {
            if (!mutex.WaitOne(LockWait)) throw new TimeoutException("notifications file is locked");
        }
        catch (AbandonedMutexException) { /* the previous owner died; the file is replaced atomically, so it is intact */ }
        catch { mutex.Dispose(); throw; }
        return new Releaser(mutex);
    }

    sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); }
        }
    }
}
