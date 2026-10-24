using LegionModes.Core.Sensors;
using LibreHardwareMonitor.Hardware;

namespace LegionModes.Tray;

// The NVIDIA video memory temperature and GPU core load through LibreHardwareMonitor (NVAPI). Only the GPU hardware group is enabled, so no kernel
// driver is loaded. Opening and reading wake a sleeping card: SensorLoop calls this only while the controller reports the card awake.
public sealed class LhmMemoryReader : IMemoryTempReader
{
    readonly Computer _computer = new() { IsGpuEnabled = true };

    public LhmMemoryReader() => _computer.Open();

    public GpuReading Read()
    {
        int? memory = null, load = null;
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.GpuNvidia) continue;
            hardware.Update();
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.Value is not float v) continue;
                if (sensor.SensorType == SensorType.Temperature && memory is null
                    && sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) && v is > 0 and < 150)
                    memory = (int)Math.Round(v);
                else if (sensor.SensorType == SensorType.Load && load is null && sensor.Name == "GPU Core" && v is >= 0 and <= 100)
                    load = (int)Math.Round(v);
            }
        }
        return new GpuReading(memory, load);
    }

    public void Dispose() => _computer.Close();
}

public sealed class LhmMemoryReaderFactory : IMemoryTempReaderFactory
{
    public IMemoryTempReader Open() => new LhmMemoryReader();
}
