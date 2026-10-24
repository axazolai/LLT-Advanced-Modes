using System.Text.Json;
using System.Text.Json.Serialization;

namespace LegionModes.Core.Ipc;

public abstract record Message;
public sealed record Heartbeat(DateTimeOffset At, bool LidOpen, bool ExternalDisplay, bool HasControl) : Message;
public sealed record Loaded(string LltVersion, string PluginVersion) : Message;
public sealed record GracefulExit : Message;
public sealed record ExitRequest : Message;                             // keeper/installer -> plugin: close LLT
public sealed record SelfTestReport(bool Ok, IReadOnlyList<string> Failures, IReadOnlyList<string>? Warnings = null) : Message;
public sealed record UpdateInProgress(bool Active) : Message;
public sealed record UpdateRequest(string Version) : Message;           // owner pressed Update
public sealed record CheckRequest : Message;                            // owner pressed "check now"
public sealed record KeeperAlive(DateTimeOffset At) : Message;
public enum QuickActionKind { WorkloadAuto, WorkloadHeavy, WorkloadAi, WorkloadWork, WorkloadIdle, Autonomy, StayAwake,
    RapidCharge, ChargeFull, Microphone, Pause, DGpu }
public sealed record QuickAction(QuickActionKind Kind) : Message;       // desktop menu -> plugin, on the quick pipe only
public sealed record ReleaseGpu(int Seconds) : Message;                 // plugin -> keeper: the card is about to be ejected, let go of it
public sealed record GpuLiveRequest : Message;                          // plugin -> keeper: the card's state now, and read it every step while asked
public sealed record GpuLiveReply(bool Awake, int? TempC, int? MemoryC, int? LoadPercent, DateTimeOffset At) : Message;
public sealed record Ack(bool Ok, string? Error = null) : Message;

public static class MessageCodec
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    static readonly Dictionary<string, Type> Types = typeof(Message).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(Message)) && !t.IsAbstract)
        .ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static byte[] Encode(Message m)
    {
        var type = m.GetType();
        if (!Types.ContainsKey(type.Name)) throw new ArgumentException($"unknown message type {type.Name}", nameof(m));
        var body = JsonSerializer.SerializeToElement(m, type, Options);
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(type.Name, body), Options);
    }

    public static Message Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(bytes, Options)
                      ?? throw new InvalidDataException("empty message");
            if (string.IsNullOrEmpty(env.Type)) throw new InvalidDataException("message has no type");
            if (!Types.TryGetValue(env.Type, out var type)) throw new InvalidDataException($"unknown message type '{env.Type}'");
            if (env.Body is null || env.Body.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("message has no body");
            return (Message?)env.Body.Value.Deserialize(type, Options) ?? throw new InvalidDataException("empty body");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("malformed message: " + e.Message, e);
        }
    }

    sealed record Envelope(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("body")] JsonElement? Body);
}
