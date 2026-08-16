using System.IO;
using System.Text.Json;

namespace NML.Core.Update;

/// <summary>One downloadable asset attached to a GitHub release.</summary>
public sealed class UpdateAsset
{
    public string Name { get; init; } = string.Empty;
    /// <summary>The direct download URL (browser_download_url) for this asset.</summary>
    public string Url { get; init; } = string.Empty;
    public long Size { get; init; }
    /// <summary>GitHub-reported SHA-256 ("sha256:&lt;hex&gt;"), when available, to verify downloads.</summary>
    public string Digest { get; init; } = string.Empty;
}

/// <summary>Information about a new release available for download.</summary>
public sealed class UpdateInfo
{
    public string TagName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty; // release notes
    public DateTimeOffset PublishedAt { get; init; }
    /// <summary>True when this release is newer than the running version.</summary>
    public bool IsNewer { get; init; }
    /// <summary>True when the release is marked as a pre-release (never the latest stable).</summary>
    public bool IsPrerelease { get; init; }
    /// <summary>Downloadable assets attached to the release (installer/exe/zip). May be empty.</summary>
    public IReadOnlyList<UpdateAsset> Assets { get; init; } = Array.Empty<UpdateAsset>();
}

/// <summary>
/// Checks GitHub Releases for a newer version of the launcher. Compares the latest release
/// tag against the running version using semantic versioning. Pure logic — the HTTP fetch is
/// injected so tests can stub it.
/// <para>
/// Robust against network failure, malformed/empty JSON, and missing fields: <see cref="CheckAsync"/>
/// returns null (treated as "check failed / no info") rather than throwing, so a flaky network
/// never breaks the startup flow. It parses the release's <c>assets[]</c> so a caller can download
/// a binary update directly instead of only opening the release web page.
/// </para>
/// </summary>
public sealed class UpdateChecker
{
    private readonly Func<string, CancellationToken, Task<string?>> _fetchJson;

    public string RepoOwner { get; }
    public string RepoName { get; }

    public UpdateChecker(string repoOwner, string repoName, Func<string, CancellationToken, Task<string?>> fetchJson)
    {
        RepoOwner = repoOwner;
        RepoName = repoName;
        _fetchJson = fetchJson;
    }

    /// <summary>Fetch the latest release and compare against <paramref name="currentVersion"/>.
    /// Returns null when the network call fails, returns empty, or the response is not valid JSON —
    /// so callers can treat null as "check unavailable" without try/catch.</summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

        string? json;
        try { json = await _fetchJson(url, ct); }
        catch { return null; } // network error / timeout → "no update info"
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return null; } // malformed / non-JSON (e.g. a 403 rate-limit HTML body) → null
        using (doc)
        {
            var root = doc.RootElement;

            // A release with no tag_name is unusable — treat as unavailable rather than as version "".
            string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(tag)) return null;

            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? (n.GetString() ?? tag) : tag;
            string htmlUrl = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String
                ? (h.GetString() ?? "") : "";
            string body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? (b.GetString() ?? "") : "";
            DateTimeOffset published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetDateTimeOffset() : DateTimeOffset.UtcNow;
            bool prerelease = root.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.False
                ? false : (pr.ValueKind == JsonValueKind.True);

            var assets = ParseAssets(root);

            return new UpdateInfo
            {
                TagName = tag,
                Name = name,
                HtmlUrl = htmlUrl,
                Body = body,
                PublishedAt = published,
                IsNewer = IsVersionNewer(tag, currentVersion),
                IsPrerelease = prerelease,
                Assets = assets,
            };
        }
    }

    /// <summary>Parse the assets[] array of a release into <see cref="UpdateAsset"/> objects.</summary>
    private static IReadOnlyList<UpdateAsset> ParseAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<UpdateAsset>();
        var list = new List<UpdateAsset>();
        foreach (var a in arr.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String
                ? (an.GetString() ?? "") : "";
            string url = a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                ? (u.GetString() ?? "") : "";
            long size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long s) ? s : 0;
            // GitHub reports the asset SHA-256 digest as "sha256:<hex>" — used to verify the
            // download before any self-replace (supply-chain guard).
            string digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String
                ? (dg.GetString() ?? "") : "";
            if (!string.IsNullOrEmpty(url)) list.Add(new UpdateAsset { Name = name, Url = url, Size = size, Digest = digest });
        }
        return list;
    }

    /// <summary>
    /// Download a release asset by name into <paramref name="destination"/>. Returns the written path
    /// on success. Used by the "download update" flow so the user can fetch the new binary without
    /// leaving the launcher.
    /// </summary>
    public async Task<string> DownloadAssetToAsync(UpdateAsset asset, string destination, StreamToAsync streamTo, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var fs = File.Create(destination);
        await streamTo(asset.Url, fs, null, ct);
        return destination;
    }

    /// <summary>Stream-sink delegate matching <see cref="NML.Core.Download.IHttpFetcher.StreamToAsync"/>.</summary>
    public delegate Task StreamToAsync(string url, Stream destination, IProgress<long>? progress, CancellationToken ct);

    /// <summary>
    /// Compare a release tag (e.g. "v0.2.0") against the current version (e.g. "0.1.0").
    /// Uses simple numeric component comparison (major.minor.patch). Returns true if the tag
    /// is strictly newer.
    /// </summary>
    public static bool IsVersionNewer(string releaseTag, string currentVersion)
    {
        var rel = ParseVersion(releaseTag);
        var cur = ParseVersion(currentVersion);

        if (rel.Major != cur.Major) return rel.Major > cur.Major;
        if (rel.Minor != cur.Minor) return rel.Minor > cur.Minor;
        return rel.Patch > cur.Patch;
    }

    /// <summary>Parse a version string like "v0.2.0" or "0.2.0-alpha" into (major, minor, patch).</summary>
    public static (int Major, int Minor, int Patch) ParseVersion(string s)
    {
        // Strip a leading 'v' and any pre-release suffix after '-'.
        string clean = s.TrimStart('v').Split('-')[0];
        var parts = clean.Split('.');
        int major = parts.Length > 0 && int.TryParse(parts[0], out int ma) ? ma : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int mi) ? mi : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int pa) ? pa : 0;
        return (major, minor, patch);
    }
}
