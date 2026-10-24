namespace LegionModes.Core.Sensors;

// The digit colour zone of a temperature (owner, 2026-10-22).
public static class Heat
{
    public static HeatZone For(Sensor sensor, int c) => sensor == Sensor.Cpu
        ? (c >= 90 ? HeatZone.Red : c >= 80 ? HeatZone.Orange : c >= 60 ? HeatZone.Yellow : HeatZone.Green)
        : (c >= 75 ? HeatZone.Red : c >= 70 ? HeatZone.Orange : c >= 60 ? HeatZone.Yellow : HeatZone.Green);
}
