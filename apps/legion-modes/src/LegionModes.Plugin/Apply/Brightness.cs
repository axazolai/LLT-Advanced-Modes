using System.Management;

namespace LegionModes.Plugin.Apply;

internal static class Brightness
{
    // The highest brightness level the internal panel reports (WmiMonitorBrightness exists for the internal panel only);
    // 100 when the query fails or reports nothing.
    public static int Max()
    {
        try
        {
            var max = 0;
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT Level FROM WmiMonitorBrightness");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                    if (o["Level"] is byte[] { Length: > 0 } levels) max = Math.Max(max, levels.Max());
            }
            return max > 0 ? max : 100;
        }
        catch (Exception e)
        {
            Log.Error("read panel brightness levels", e);
            return 100;
        }
    }
}
