using System.Globalization;

namespace LegionModes.Core.Sensors;

// The tooltips of the two icons, in the interface language (Tr). A tray tooltip holds at most 127 characters.
public static class TooltipText
{
    public const int MaxLength = 127;

    public static string Cpu(ControllerSample s, int? peak, bool overDay)
    {
        var lines = new List<string> { Tr.F("CPU {0} °C", Show(s.CpuC > 0 ? s.CpuC : null)) };
        if (s.ChipsetC is > 0 and var chipset) lines.Add(Tr.F("Chipset {0} °C", chipset));
        if (s.CpuFanRpm is { } fan) lines.Add(Tr.F("Fan {0} rpm", fan));
        if (peak is { } p) lines.Add(PeakLine(p, overDay));
        return Join(lines);
    }

    // memoryC null = the card gave no video memory temperature: the line is left out.
    public static string Dgpu(ControllerSample s, int? memoryC, int? peak, bool overDay)
    {
        var lines = new List<string> { Tr.F("dGPU {0} °C", Show(s.DgpuC > 0 ? s.DgpuC : null)) };
        if (memoryC is { } memory) lines.Add(Tr.F("Memory {0} °C", memory));
        if (s.GpuFanRpm is { } fan) lines.Add(Tr.F("Fan {0} rpm", fan));
        if (peak is { } p) lines.Add(PeakLine(p, overDay));
        return Join(lines);
    }

    static string PeakLine(int c, bool overDay) =>
        overDay ? Tr.F("Peak over the last 24 h: {0} °C", c) : Tr.F("Peak since session start: {0} °C", c);

    static string Show(int? c) => c is { } v ? v.ToString(CultureInfo.InvariantCulture) : "--";

    static string Join(List<string> lines)
    {
        var text = string.Join("\n", lines);
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}
