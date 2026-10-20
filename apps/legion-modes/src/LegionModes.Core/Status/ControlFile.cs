namespace LegionModes.Core.Status;

// control.json in the plugin data dir. The saved values are what Return restores; Scheme is the power scheme that was
// active at Take, and the lid action is written to and restored in that scheme. Fields after Scheme are optional in the
// file: a record written by an older build loads with their defaults.
public sealed record ControlData(bool HasControl, uint? SavedLidActionAc, uint? SavedLidActionDc, bool? SavedAutomationEnabled,
                                 string? SavedPowerModeMappingMode, Guid? Scheme = null, bool Autonomy = false, bool? CameraOff = null,
                                 DateTimeOffset? CameraAt = null, int AbnormalShutdowns = 0, bool Running = false,
                                 IReadOnlyList<string>? WakeOverrides = null)
{
    public static ControlData Empty { get; } = new(false, null, null, null, null);
}

public sealed record ControlRead(ControlData Data, bool Missing, string? Error);

public static class ControlFile
{
    // A missing or unreadable file gives the empty record (no control, counter 0, nothing running).
    public static ControlData Load(string path)
    {
        var d = JsonFile.Load<ControlData>(path) ?? ControlData.Empty;
        return d.WakeOverrides is null ? d with { WakeOverrides = [] } : d;
    }

    // Missing (the empty record) is told apart from unreadable (Error set; the record must not be written over).
    public static ControlRead Read(string path)
    {
        var (value, missing, error) = JsonFile.TryLoad<ControlData>(path);
        if (error is not null) return new ControlRead(ControlData.Empty, false, error);
        var d = value ?? ControlData.Empty;
        return new ControlRead(d.WakeOverrides is null ? d with { WakeOverrides = [] } : d, missing, null);
    }

    public static void Save(string path, ControlData d) => JsonFile.Save(path, d);
}
