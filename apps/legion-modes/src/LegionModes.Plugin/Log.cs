using LenovoLegionToolkit.Lib.Station.Logging;

namespace LegionModes.Plugin;

// LLT's extension logger; every call is safe before Init and never throws.
internal static class Log
{
    static volatile IExtensionLogger? _logger;

    public static void Init(IExtensionLogger logger) => _logger = logger;

    public static void Trace(string message)
    {
        try { _logger?.Trace("[LegionModes] " + message); } catch { }
    }

    public static void Error(string message, Exception e)
    {
        try { _logger?.Error("[LegionModes] " + message, e); } catch { }
    }
}
