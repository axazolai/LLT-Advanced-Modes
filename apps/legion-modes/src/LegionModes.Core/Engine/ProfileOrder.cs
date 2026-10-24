using LegionModes.Core.Model;

namespace LegionModes.Core.Engine;

public static class ProfileOrder
{
    // A tree walk: the root first, «Автономия» last, and every automatic profile right under its parent, the one of its direct
    // parents (Resolver.Parents) that stands last in the list; profiles with no parent but the root stand at the top. Siblings
    // keep the owner's order.
    public static IReadOnlyList<Profile> ByHierarchy(ModesConfig c)
    {
        var autos = c.Profiles.Where(p => p.Kind == ProfileKind.Automatic).ToArray();
        var children = autos.ToLookup(p => Resolver.Parents(c, p).Where(q => q.Kind == ProfileKind.Automatic)
            .OrderByDescending(c.IndexOf).FirstOrDefault()?.Id);
        var result = new List<Profile>(c.Profiles.Where(p => p.Kind == ProfileKind.Root));
        void Walk(Guid? parent)
        {
            foreach (var p in children[parent].OrderBy(c.IndexOf)) { result.Add(p); Walk(p.Id); }
        }
        Walk(null);
        result.AddRange(c.Profiles.Where(p => p.Kind == ProfileKind.Manual));
        return result;
    }

    // Pairs of profiles that can match together at equal specificity and change places: the one higher in the list wins, so the
    // winner of such a pair changes with the order.
    public static IReadOnlyList<(Profile Was, Profile Becomes)> Flips(IReadOnlyList<Profile> before, IReadOnlyList<Profile> after)
    {
        var pos = after.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var flips = new List<(Profile, Profile)>();
        var autos = before.Where(p => p.Kind == ProfileKind.Automatic).ToArray();
        for (var i = 0; i < autos.Length; i++)
            for (var j = i + 1; j < autos.Length; j++)
            {
                var a = autos[i]; var b = autos[j];
                if (a.Conditions.Specificity == b.Conditions.Specificity && a.Conditions.CompatibleWith(b.Conditions) && pos[a.Id] > pos[b.Id])
                    flips.Add((a, b));
            }
        return flips;
    }
}
