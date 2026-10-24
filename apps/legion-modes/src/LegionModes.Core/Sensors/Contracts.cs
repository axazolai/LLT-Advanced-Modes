namespace LegionModes.Core.Sensors;

public enum Sensor { Cpu, Dgpu }

public enum HeatZone { Green, Yellow, Orange, Red }

// Raw controller values; null = no usable value (a temperature below 0 or above 125, a failed fan read). DgpuC 0 means the dGPU is asleep.
public sealed record ControllerSample(int? CpuC, int? ChipsetC, int? DgpuC, int? CpuFanRpm, int? GpuFanRpm);

// What one tray icon shows. Zone null = the failure look (grey digits, "--").
public sealed record IconState(string Digits, HeatZone? Zone, string Tooltip);

public interface IControllerReader
{
    // includeDgpu false: DgpuC of the result is null and the controller is not asked for it. Throws when the controller cannot be read.
    ControllerSample Read(bool includeDgpu);

    // Drops any cached controller handle; the next Read starts afresh (after a failure, after sleep).
    void Reset();
}

public interface IMemoryTempReader : IDisposable
{
    // The video memory temperature in whole degrees and the GPU core load in percent, each null when the card gives none.
    GpuReading Read();
}

public sealed record GpuReading(int? MemoryC, int? LoadPercent);

// What the loop last saw of the discrete card: awake (the controller reports it above zero), its temperature, the video memory
// temperature and the core load when the NVIDIA stack was asked.
public sealed record GpuLive(bool Awake, int? TempC, int? MemoryC, int? LoadPercent);

public interface IMemoryTempReaderFactory
{
    // Opens the NVIDIA stack. Only called while the dGPU is awake.
    IMemoryTempReader Open();
}

public interface ITrayView
{
    void Show(Sensor sensor, IconState state);
    void Hide(Sensor sensor);
}
