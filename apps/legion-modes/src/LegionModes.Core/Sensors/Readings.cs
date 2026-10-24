namespace LegionModes.Core.Sensors;

// The range check of the controller values: a temperature outside 0..125 and a fan speed outside 0..20000 is "no value" (null).
// A 0 stays: dGPU 0 means asleep, CPU 0 is handled by the loop.
public static class Readings
{
    public const int MaxTempC = 125;
    public const int MaxRpm = 20000;

    public static ControllerSample Normalize(ControllerSample s) => new(
        Temp(s.CpuC), Temp(s.ChipsetC), Temp(s.DgpuC), Rpm(s.CpuFanRpm), Rpm(s.GpuFanRpm));

    static int? Temp(int? v) => v is < 0 or > MaxTempC ? null : v;

    static int? Rpm(int? v) => v is < 0 or > MaxRpm ? null : v;
}
