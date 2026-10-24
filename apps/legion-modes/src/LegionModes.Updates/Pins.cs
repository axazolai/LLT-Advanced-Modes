using System.Text.Json.Nodes;

namespace LegionModes.Updates;

// Values pinned in llt.lock.json, embedded in this assembly.
public static class Pins
{
    private static readonly Lazy<string> Signer = new(() =>
    {
        using var s = typeof(Pins).Assembly.GetManifestResourceStream("llt.lock.json")
                      ?? throw new InvalidOperationException("llt.lock.json is not embedded");
        var node = JsonNode.Parse(s)!;
        return node["llt"]!["signerThumbprint"]!.GetValue<string>();
    });

    private static readonly Lazy<string> Version = new(() =>
    {
        using var s = typeof(Pins).Assembly.GetManifestResourceStream("llt.lock.json")
                      ?? throw new InvalidOperationException("llt.lock.json is not embedded");
        return JsonNode.Parse(s)!["llt"]!["version"]!.GetValue<string>();
    });

    public static string SignerThumbprint => Signer.Value;

    // The LLT version this build is made for; nothing installs another one.
    public static string LltVersion => Version.Value;
}
