using System.Text.Json.Serialization;

namespace LegionModes.Core.Model;

public sealed record ModesConfig(int SchemaVersion, IReadOnlyList<Profile> Profiles, int PauseMinutes = 60)
{
    public const int CurrentSchema = 2;

    [JsonIgnore] public Profile Root => Profiles[0];
    [JsonIgnore] public Profile Autonomy => Profiles.Single(p => p.Kind == ProfileKind.Manual);
    public int IndexOf(Profile p) => Profiles.ToList().FindIndex(x => x.Id == p.Id);
}
