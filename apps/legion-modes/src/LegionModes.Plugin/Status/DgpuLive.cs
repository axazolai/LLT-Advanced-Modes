using System.Management;
using LegionModes.Core;
using LegionModes.Core.Ipc;
using LegionModes.Plugin.Ui.ViewModels;

namespace LegionModes.Plugin.Status;

// What the discrete card is doing right now, for the Status tab. Nothing here depends on LLT: the temperature comes from the laptop's
// embedded controller (0 while the card sleeps, as the keeper's tray icon reads it), whether Windows still lists the card from the device
// list, and the core load from the keeper, which asks the card only while this tab is on screen and the card is awake (the way LLT's own
// GPU block polls while it is shown). A hidden tab polls nothing that touches the card.
public sealed class DgpuLive : IDisposable
{
    public const int BusyLoadPercent = 10;   // the keeper's ActiveLoadPercent
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    static readonly TimeSpan KeeperFresh = TimeSpan.FromSeconds(8);
    static readonly TimeSpan KeeperWait = TimeSpan.FromSeconds(1.5);
    const uint DgpuId = 0x05050000;

    readonly object _gate = new();
    System.Threading.Timer? _timer;
    ManagementObject? _ec;
    bool? _available;
    int? _ecTemp;
    int? _gsync, _igpuMode, _smartFan;
    GpuLiveReply? _keeper;
    DateTimeOffset _keeperAt = DateTimeOffset.MinValue;
    volatile bool _watching;
    int _reading;
    int _disposed;

    public void Start() => _timer = new System.Threading.Timer(_ => _ = ReadAsync(), null, TimeSpan.Zero, Poll);

    // The tab is on screen or not: only while it is, the keeper is asked (and then reads the card every step).
    public void Watch(bool on)
    {
        _watching = on;
        if (on) _ = ReadAsync();
    }

    async Task ReadAsync()
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _reading, 1) == 1) return;
        try
        {
            var (available, temp) = await Task.Run(ReadSystem).ConfigureAwait(false);
            GpuLiveReply? reply = null;
            if (_watching && temp is > 0)
            {
                try { reply = await PipeClient.SendAsync(Paths.KeeperPipe, new GpuLiveRequest(), KeeperWait).ConfigureAwait(false) as GpuLiveReply; }
                catch (Exception e) { Log.Error("dGPU live: keeper", e); }
            }
            lock (_gate)
            {
                _available = available;
                _ecTemp = temp;
                if (reply is not null) { _keeper = reply; _keeperAt = DateTimeOffset.Now; }
            }
        }
        catch (Exception e) { Log.Error("dGPU live read", e); }
        finally { Volatile.Write(ref _reading, 0); }
    }

    (bool? Available, int? Temp) ReadSystem()
    {
        var read = (ReadAvailable(), ReadEcTemp());
        ReadGameZone();
        return read;
    }

    // The firmware's own answers about the GPU mode and the power mode (LENOVO_GAMEZONE_DATA): what is set now, whatever LLT has cached.
    ManagementObject? _gz;

    void ReadGameZone()
    {
        try
        {
            if (_gz is null)
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM LENOVO_GAMEZONE_DATA");
                using var all = searcher.Get();
                foreach (ManagementObject o in all) { _gz = o; break; }
                if (_gz is null) return;
            }
            int Get(string method)
            {
                using var output = _gz.InvokeMethod(method, null, null);
                return Convert.ToInt32(output["Data"]);
            }
            int gsync = Get("GetGSyncStatus"), igpu = Get("GetIGPUModeStatus"), fan = Get("GetSmartFanMode");
            lock (_gate) { _gsync = gsync; _igpuMode = igpu; _smartFan = fan; }
        }
        catch (Exception e)
        {
            _gz?.Dispose();
            _gz = null;
            lock (_gate) { _gsync = _igpuMode = _smartFan = null; }
            Log.Error("dGPU live: firmware read", e);
        }
    }

    public LegionModes.Core.Status.FirmwareGpu? FirmwareGpu { get { lock (_gate) return LegionModes.Core.Status.FirmwareFacts.Gpu(_gsync, _igpuMode); } }
    public LegionModes.Core.Status.FirmwarePower? FirmwarePower { get { lock (_gate) return LegionModes.Core.Status.FirmwareFacts.Power(_smartFan); } }

    // Windows lists the NVIDIA display device with a problem code 45 ("currently not connected") or not at all once the card is ejected.
    static bool? ReadAvailable()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2",
                "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Display' AND Name LIKE 'NVIDIA%'");
            using var all = searcher.Get();
            foreach (ManagementObject o in all)
            {
                using (o) { if (Convert.ToInt32(o["ConfigManagerErrorCode"]) == 0) return true; }
            }
            return false;
        }
        catch (Exception e)
        {
            Log.Trace("dGPU live: device list not read: " + e.Message);   // the list changes under the query when the card goes or comes
            return null;
        }
    }

    // The embedded controller's dGPU temperature in whole degrees: 0 = the card sleeps, null = the controller could not be read.
    int? ReadEcTemp()
    {
        try
        {
            if (_ec is null)
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM LENOVO_OTHER_METHOD");
                using var all = searcher.Get();
                foreach (ManagementObject o in all) { _ec = o; break; }
                if (_ec is null) return null;
            }
            using var input = _ec.GetMethodParameters("GetFeatureValue");
            input["IDs"] = DgpuId;
            using var output = _ec.InvokeMethod("GetFeatureValue", input, null);
            var v = Convert.ToInt32(output["value"]);
            return v is >= 0 and <= 125 ? v : null;
        }
        catch (Exception e)
        {
            _ec?.Dispose();
            _ec = null;
            Log.Error("dGPU live: controller read", e);
            return null;
        }
    }

    public (string Text, Tone Tone) Current()
    {
        lock (_gate) return Describe(_available, _ecTemp, _keeper, DateTimeOffset.Now - _keeperAt);
    }

    // The words and the colour for one reading. available false = Windows no longer lists the card; ecTemp 0 = the card sleeps;
    // keeper = the core load the keeper read, shown only while it is fresh.
    public static (string Text, Tone Tone) Describe(bool? available, int? ecTemp, GpuLiveReply? keeper, TimeSpan keeperAge)
    {
        if (available == false) return (Tr.T("off, not visible to the system"), Tone.Good);
        if (ecTemp is null) return (Tr.T("no data"), Tone.Muted);
        if (ecTemp == 0) return (Tr.T("asleep, powered down"), Tone.Good);
        var text = Tr.F("on, {0} °C", ecTemp.Value);
        if (keeper is { LoadPercent: { } load } && keeperAge <= KeeperFresh)
            return (text + ", " + (load >= BusyLoadPercent ? Tr.T("busy") : Tr.T("idle")) + " " + load + " %", load >= BusyLoadPercent ? Tone.Warn : Tone.Normal);
        return (text, Tone.Normal);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _watching = false;
        _timer?.Dispose();
        _ec?.Dispose();
        _gz?.Dispose();
    }
}
