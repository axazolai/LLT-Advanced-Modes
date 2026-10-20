namespace LegionModes.Core.Model;

public sealed record Range(int Min, int Max, int Step);

public static class FirmwareRanges                                     // refs/mode-defaults.md section 1
{
    public static readonly Range CpuLong = new(50, 120, 1), CpuShort = new(60, 145, 1), CpuPeak = new(70, 160, 1),
        CpuCross = new(30, 100, 1), CpuTemp = new(85, 100, 1), GpuTemp = new(75, 87, 1), Tpp = new(35, 85, 1),
        CurveOptimizer = new(0, 20, 1), Epp = new(0, 100, 1);
    public static readonly int[] DynamicBoost = [0, 15, 20, 25];
    public static readonly int[] ConfigurableTgp = [80, 85, 90, 95, 100, 105, 110, 115, 120, 125, 130, 135, 140, 145, 150, 155, 160];
    // Measured on the Legion Pro 7 in GodMode (Task 30): the firmware raises every point to this floor, so a lower curve never reads back.
    public static readonly int[] FanMinimum = [1, 2, 3, 4, 5, 6, 7, 8, 8, 8];
    public static readonly int[] PanelHz = [60, 75, 100, 120, 240];
}
