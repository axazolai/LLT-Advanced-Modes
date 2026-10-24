using System.Management;
using LegionModes.Core.Sensors;

namespace LegionModes.Tray;

// The laptop's embedded controller through WMI (root\wmi, LENOVO_OTHER_METHOD.GetFeatureValue), the way LLT reads it.
// Ids measured on a Legion Pro 7 16ARX8H (82WS) on 2026-10-22: 0x05040000 CPU, 0x05010000 chipset (PCH), 0x05050000 dGPU (0 while it sleeps),
// 0x04030001 CPU fan rpm, 0x04030002 GPU fan rpm. Returns the raw values; the range checks live in Readings (Core).
public sealed class EcReader : IControllerReader, IDisposable
{
    const uint CpuId = 0x05040000, ChipsetId = 0x05010000, DgpuId = 0x05050000, CpuFanId = 0x04030001, GpuFanId = 0x04030002;
    ManagementObject? _object;

    public ControllerSample Read(bool includeDgpu)
    {
        var o = Instance();
        return new ControllerSample(
            Raw(o, CpuId),
            Raw(o, ChipsetId),
            includeDgpu ? Raw(o, DgpuId) : null,
            Raw(o, CpuFanId),
            Raw(o, GpuFanId));
    }

    public void Reset()
    {
        _object?.Dispose();
        _object = null;
    }

    public void Dispose() => Reset();

    ManagementObject Instance()
    {
        if (_object is not null) return _object;
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM LENOVO_OTHER_METHOD");
        using var all = searcher.Get();
        foreach (ManagementObject o in all)
        {
            _object = o;   // the first (only) instance; the collection is disposed, the object is kept
            return o;
        }
        throw new InvalidOperationException("LENOVO_OTHER_METHOD has no instance");
    }

    static int Raw(ManagementObject o, uint id)
    {
        using var input = o.GetMethodParameters("GetFeatureValue");
        input["IDs"] = id;
        using var output = o.InvokeMethod("GetFeatureValue", input, null);
        return Convert.ToInt32(output["value"]);
    }

}
