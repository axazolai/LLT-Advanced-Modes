namespace LegionModes.Core.Model;

public enum PowerSource { Ac, Weak, Battery }
public enum LidState { Open, Closed }
public enum Workload { Idle = 0, Work = 1, Ai = 2, Heavy = 3 }          // ordered: higher wins
public enum GpuMode { Hybrid, HybridIgpuOnly, HybridAuto }
public enum ChargingMode { DoNotTouch, Conservation, Normal, Rapid }
public enum UsbPower { Off, WhenSleeping, Always }
public enum Projection { DoNotTouch, Extend, ExternalOnly, Duplicate, InternalOnly }
public enum ExternalRateKind { DoNotTouch, Maximum, Fixed }

public sealed record ExternalRate(ExternalRateKind Kind, int Hz = 0);
public sealed record Lighting(int Profile, int Brightness);                                       // 1..6, 0..9 (0 = off)

// Error != null => Value is the fallback; Notice: a migration happened, Value is to be saved over the file, which is kept
// first under Backup.
public sealed record LoadResult<T>(T Value, string? Error, string? Notice = null, string? Backup = null);
