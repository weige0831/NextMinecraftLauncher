using NML.Core.Download;

namespace NML.Core.Skins;

/// <summary>
/// Builds URLs for rendering a Minecraft player's skin. Uses Crafatar (the standard community
/// skin-rendering service) for avatars, 3D head renders, and full-body renders. Falls back to
/// the default skin (Steve/Alex) for offline accounts whose UUID has no real skin attached.
/// Also downloads the raw skin PNG (for the launcher's own 3D cube preview).
/// </summary>
public sealed class SkinService
{
    private readonly IHttpFetcher? _http;
    private readonly string? _cacheDir;

    /// <summary>Skin-texture provider URL templates ({0}=normalized UUID), in fallback order.
    /// crafatar.com occasionally goes down (observed Cloudflare 521), so the texture download
    /// falls through the mirrors. NOTE the path shapes differ: crafatar uses <c>/skins/</c>,
    /// mc-heads and minotar use <c>/skin/</c>. (crafatar.org is dead — 000/no-route.)</summary>
    public static readonly IReadOnlyList<string> ProviderTemplates = new[]
    {
        "https://crafatar.com/skins/{0}",
        "https://mc-heads.net/skin/{0}",
        "https://minotar.net/skin/{0}",
    };

    /// <summary>Primary provider base (kept for compatibility + tests).</summary>
    public const string CrafatarBase = "https://crafatar.com";

    /// <summary>Construct a URL-only service (no caching). Use the other ctor to enable downloads.</summary>
    public SkinService() { }

    /// <summary>Construct a service that can download raw skin PNGs into <paramref name="cacheDir"/>.</summary>
    public SkinService(IHttpFetcher http, string cacheDir)
    {
        _http = http;
        _cacheDir = cacheDir;
    }

    /// <summary>
    /// Download the raw 64×64 skin PNG for <paramref name="uuid"/> into the cache and return its
    /// absolute path. Used by the launcher's own 3D cube preview (we render the skin ourselves
    /// rather than relying on Crafatar's static renders). Tries each provider mirror in turn;
    /// falls back to the Steve default on total failure (offline UUIDs, network errors).
    /// </summary>
    public async Task<string> DownloadSkinPngAsync(string uuid, CancellationToken ct = default)
    {
        if (_http is null || _cacheDir is null)
            throw new InvalidOperationException("SkinService was constructed without a cache; cannot download.");

        Directory.CreateDirectory(_cacheDir);
        string path = Path.Combine(_cacheDir, Normalize(uuid) + ".png");
        if (File.Exists(path)) return path; // idempotent

        ValidateUuid(uuid);
        string id = Normalize(uuid);
        foreach (string template in ProviderTemplates)
        {
            try
            {
                byte[] png = await _http.GetByteArrayAsync(string.Format(template, id), ct);
                if (png.Length > 0)
                {
                    await File.WriteAllBytesAsync(path, png, ct);
                    return path;
                }
            }
            catch
            {
                // This provider is down or has no skin for the UUID — try the next mirror.
            }
        }
        // Caller will detect the missing file and fall back to a default-skin path.
        return string.Empty;
    }

    /// <summary>
    /// Build a 2D avatar (the player's face) URL.
    /// </summary>
    /// <param name="uuid">Player UUID (with or without dashes).</param>
    /// <param name="size">Image size in pixels (8–512).</param>
    public string AvatarUrl(string uuid, int size = 64)
    {
        ValidateUuid(uuid);
        return $"{CrafatarBase}/avatars/{Normalize(uuid)}?size={ClampSize(size)}&overlay";
    }

    /// <summary>
    /// Build a 3D head render URL (isometric, the classic launcher look).
    /// </summary>
    public string HeadRenderUrl(string uuid, int scale = 4)
    {
        ValidateUuid(uuid);
        return $"{CrafatarBase}/renders/head/{Normalize(uuid)}?scale={ClampScale(scale)}&overlay";
    }

    /// <summary>
    /// Build a 3D full-body render URL (the player's whole skin).
    /// </summary>
    public string BodyRenderUrl(string uuid, int scale = 4)
    {
        ValidateUuid(uuid);
        return $"{CrafatarBase}/renders/body/{Normalize(uuid)}?scale={ClampScale(scale)}&overlay";
    }

    /// <summary>
    /// Build skin-texture download URLs (raw skin PNG) for every provider mirror, in fallback
    /// order. Consumers that fetch images themselves can walk this list instead of hardcoding
    /// the primary provider.
    /// </summary>
    public IReadOnlyList<string> SkinTextureUrls(string uuid)
    {
        ValidateUuid(uuid);
        string id = Normalize(uuid);
        return ProviderTemplates.Select(t => string.Format(t, id)).ToList();
    }

    /// <summary>
    /// Build a skin-texture download URL (the raw skin PNG from the primary provider).
    /// </summary>
    public string SkinTextureUrl(string uuid)
    {
        ValidateUuid(uuid);
        return $"{CrafatarBase}/skins/{Normalize(uuid)}";
    }

    /// <summary>Normalize a UUID: strip dashes so Crafatar accepts both forms.</summary>
    public static string Normalize(string uuid) =>
        uuid.Replace("-", string.Empty).ToLowerInvariant();

    /// <summary>Determine whether a UUID is a real online UUID (and thus likely to have a skin).</summary>
    /// <remarks>
    /// Offline UUIDs are MD5-derived (version 3); Mojang online UUIDs are version 4. If the
    /// account is offline, Crafatar returns the default skin — but we can short-circuit by
    /// checking the version nibble.
    /// </remarks>
    public static bool IsLikelyOfflineUuid(string uuid)
    {
        string n = Normalize(uuid);
        if (n.Length < 13) return true;
        // Version nibble is the 13th hex char. '3' = v3 (MD5/offline), '4' = v4 (random/online).
        char version = n[12];
        return version != '4';
    }

    private static void ValidateUuid(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            throw new ArgumentException("UUID is required.", nameof(uuid));
    }

    private static int ClampSize(int size) => Math.Clamp(size, 8, 512);
    private static int ClampScale(int scale) => Math.Clamp(scale, 1, 16);
}
