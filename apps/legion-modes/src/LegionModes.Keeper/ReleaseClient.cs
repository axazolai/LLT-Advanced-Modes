using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using LegionModes.Updates;

namespace LegionModes.Keeper;

internal sealed class ReleaseClient(HttpClient http)
{
    const string ListUrl = "https://api.github.com/repos/LenovoLegionToolkit-Team/LenovoLegionToolkit/releases?per_page=30";
    static readonly Regex AssetName = new(@"^LenovoLegionToolkitSetup-v.*\.exe$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LegionModes", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    // Reads the release list (prereleases included) and returns ReleaseChoice.Pick; null when nothing is newer than installed.
    // Throws on network or rate limit, and when the chosen release lacks the expected asset or digest; the caller turns
    // that into CheckOutcome.Error. Drafts and releases with a tag that is not a version are skipped.
    public async Task<ReleaseInfo?> LatestAsync(string installed, IReadOnlyCollection<string> pluginVersions, CancellationToken ct)
    {
        using var response = await http.GetAsync(ListUrl, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException($"GitHub refused the request (HTTP {(int)response.StatusCode}, rate limit?)");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var releases = new List<ReleaseInfo>();
        var withoutAsset = new List<string>();
        foreach (var root in doc.RootElement.EnumerateArray())
        {
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v'), out _)) continue;
            var info = ToInfo(root, tag, out var problem);
            if (info is not null) releases.Add(info); else withoutAsset.Add($"{tag}: {problem}");
        }
        var pick = ReleaseChoice.Pick(releases, installed, pluginVersions);
        if (pick is not null) return pick;
        // Nothing usable is newer; a newer release that lacks the asset is an error, not "up to date".
        if (Version.TryParse(installed, out var have))
            foreach (var w in withoutAsset)
                if (Version.TryParse(w[..w.IndexOf(':')].TrimStart('v'), out var v) && v > have)
                    throw new InvalidOperationException($"release {w}");
        return null;
    }

    private static ReleaseInfo? ToInfo(JsonElement root, string tag, out string problem)
    {
        problem = "";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!AssetName.IsMatch(name)) continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                problem = $"asset {name} has no sha256 digest";
                return null;
            }
            var published = root.TryGetProperty("published_at", out var p) && p.GetString() is { } s
                ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture)
                : DateTimeOffset.UtcNow;
            var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new ReleaseInfo(tag.TrimStart('v'), tag, asset.GetProperty("browser_download_url").GetString()!,
                digest["sha256:".Length..], body, published);
        }
        problem = "has no LenovoLegionToolkitSetup-v*.exe asset";
        return null;
    }
}
