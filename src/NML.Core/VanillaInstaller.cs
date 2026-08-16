using System.IO.Compression;
using Microsoft.Extensions.Logging;
using NML.Core.Download;
using NML.Core.Models;
using NML.Core.Rules;

namespace NML.Core;

/// <summary>
/// Installs a vanilla Minecraft version end-to-end: client.jar, libraries (filtered
/// by current OS/arch rules), the asset index + asset objects, and extracts native
/// libraries into <c>bin/natives</c>. Idempotent — re-running skips already-valid files.
/// </summary>
public sealed class VanillaInstaller
{
    private readonly IHttpFetcher _http;
    private readonly Downloader _downloader;
    private readonly VersionInfoService _versions;
    private readonly ILogger<VanillaInstaller> _logger;

    public VanillaInstaller(
        IHttpFetcher http,
        Downloader downloader,
        VersionInfoService versions,
        ILogger<VanillaInstaller> logger)
    {
        _http = http;
        _downloader = downloader;
        _versions = versions;
        _logger = logger;
    }

    /// <summary>
    /// Ensure <paramref name="versionId"/> is fully installed under <paramref name="mc"/>.
    /// Returns the merged <see cref="VersionInfo"/> ready for launching.
    /// </summary>
    public async Task<VersionInfo> InstallAsync(
        string versionId,
        MinecraftDirectory mc,
        RuleContext? ruleCtx = null,
        DownloadCancel? cancel = null,
        ProgressReporter? progress = null,
        DownloadSettings? downloadSettings = null,
        CancellationToken ct = default)
    {
        // Fall back to defaults (concurrency 8/16, no mirror) when no explicit settings are passed.
        int libConcurrency = downloadSettings?.Concurrency ?? 8;
        int assetConcurrency = downloadSettings?.Concurrency is { } c && c > 0
            ? Math.Min(c * 2, DownloadSettings.MaxConcurrency) // assets are tiny; allow 2× the cap
            : 16;
        string? mirror = downloadSettings?.MirrorUrl;
        ruleCtx ??= RuleContext.Current();
        _logger.LogInformation("Installing vanilla {Id} (concurrency={Lib}, mirror={Mirror})…",
            versionId, libConcurrency, mirror ?? "official");

        VersionInfo info = await _versions.GetAsync(versionId, mc, ct);

        await DownloadClientJarAsync(info, mc, cancel, progress, mirror, ct);
        await DownloadLibrariesAsync(info, mc, ruleCtx, cancel, progress, libConcurrency, mirror, ct);
        await DownloadAssetsAsync(info, mc, cancel, progress, assetConcurrency, mirror, ct);
        await ExtractNativesAsync(info, mc, ruleCtx, ct);

        _logger.LogInformation("Install of {Id} complete.", versionId);
        return info;
    }

    /// <summary>Result of a verify/repair pass over an installed instance.</summary>
    public sealed record VerifyResult(int Checked, int Repaired);

    /// <summary>
    /// Verify an installed instance's files (client.jar, libraries, assets) and re-download any
    /// that are missing or fail their SHA-1/size check. The Downloader's idempotency check skips
    /// valid files, so re-running the install phases IS the repair; we count the files that were
    /// actually re-downloaded to report what was fixed. HMCL-style "校验/修复游戏文件".
    /// </summary>
    public async Task<VerifyResult> VerifyInstanceAsync(
        string versionId,
        MinecraftDirectory mc,
        RuleContext? ruleCtx = null,
        DownloadCancel? cancel = null,
        DownloadSettings? downloadSettings = null,
        CancellationToken ct = default)
    {
        ruleCtx ??= RuleContext.Current();
        _logger.LogInformation("Verifying instance {Id}…", versionId);

        VersionInfo info = await _versions.GetAsync(versionId, mc, ct);

        // Enumerate the expected file set to compute the "checked" count up front.
        int checkedCount = 1; // client.jar
        foreach (Library lib in info.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules, ruleCtx)) continue;
            if (lib.Downloads?.Artifact is not null) checkedCount++;
            if (lib.Natives is not null && lib.Downloads?.Classifiers is not null &&
                lib.Natives.TryGetValue(ruleCtx.OsName, out string? cls) &&
                lib.Downloads.Classifiers.TryGetValue(cls, out _)) checkedCount++;
        }
        AssetIndexRef? indexRef = info.AssetIndex;
        if (indexRef is not null)
        {
            string indexPath = mc.AssetIndexPath(indexRef.Id);
            if (File.Exists(indexPath))
            {
                string json = await File.ReadAllTextAsync(indexPath, ct);
                AssetIndex? index = System.Text.Json.JsonSerializer.Deserialize<AssetIndex>(json, JsonOptions.Default);
                checkedCount += index?.Objects.Count ?? 0;
            }
        }

        // Repair pass: wrap the fetcher so each actual HTTP download (a file the Downloader
        // deemed missing/corrupt) is counted. Valid files are skipped before any fetch happens.
        int repaired = 0;
        var countingFetcher = new CountingFetcher(
            (url, stream, progress, token) => _http.StreamToAsync(url, stream, progress, token),
            () => Interlocked.Increment(ref repaired));
        var repairDownloader = new Downloader(countingFetcher,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Downloader>.Instance);

        string? mirror = downloadSettings?.MirrorUrl;
        await DownloadClientJarWithAsync(info, mc, cancel, repairDownloader, mirror, ct);
        await DownloadLibrariesWithAsync(info, mc, ruleCtx, cancel, repairDownloader, downloadSettings?.Concurrency ?? 8, mirror, ct);
        await DownloadAssetsWithAsync(info, mc, cancel, repairDownloader,
            downloadSettings?.Concurrency is { } c && c > 0 ? Math.Min(c * 2, DownloadSettings.MaxConcurrency) : 16,
            mirror, ct);
        await ExtractNativesAsync(info, mc, ruleCtx, ct);

        _logger.LogInformation("Verify of {Id}: {Checked} checked, {Repaired} repaired.", versionId, checkedCount, repaired);
        return new VerifyResult(checkedCount, repaired);
    }

    /// <summary>An IHttpFetcher wrapper that counts every actual stream fetch (i.e. every repair).</summary>
    private sealed class CountingFetcher : IHttpFetcher
    {
        private readonly Func<string, Stream, IProgress<long>?, CancellationToken, Task> _stream;
        private readonly Action _onFetch;

        public CountingFetcher(Func<string, Stream, IProgress<long>?, CancellationToken, Task> stream, Action onFetch)
        { _stream = stream; _onFetch = onFetch; }

        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default)
        { _onFetch(); return _http2(url, ct); }
        private readonly Func<string, CancellationToken, Task<byte[]>> _http2 = (_, _) => Task.FromResult(Array.Empty<byte>());

        public Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
        public Task StreamToAsync(string url, Stream destination, IProgress<long>? bytesReceived = null, CancellationToken ct = default)
        { _onFetch(); return _stream(url, destination, bytesReceived, ct); }
        public Task<RangeResponse?> TryRangeDownloadAsync(string url, long from, long? to, CancellationToken ct = default) =>
            Task.FromResult<RangeResponse?>(null);
    }

    // Phase helpers parameterized by downloader so the normal install and the verify/repair pass
    // share exactly the same enumeration logic.

    private async Task DownloadClientJarWithAsync(VersionInfo info, MinecraftDirectory mc,
        DownloadCancel? cancel, Downloader downloader, string? mirror, CancellationToken ct)
    {
        Downloadable? client = info.Downloads?.Client;
        if (client is null)
            throw new InvalidOperationException($"Version '{info.Id}' has no client download (server-only?).");
        Directory.CreateDirectory(mc.VersionDir(info.Id));
        Downloadable fetched = mirror is null ? client : WithMirror(client, mirror);
        await downloader.DownloadAsync(fetched, info.Id + ".jar", mc.VersionDir(info.Id), cancel, null, ct);
    }

    private async Task DownloadLibrariesWithAsync(VersionInfo info, MinecraftDirectory mc, RuleContext ruleCtx,
        DownloadCancel? cancel, Downloader downloader, int libConcurrency, string? mirror, CancellationToken ct)
    {
        var toFetch = new List<(Downloadable File, string RelativePath)>();
        foreach (Library lib in info.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules, ruleCtx)) continue;
            if (lib.Downloads?.Artifact is not null)
            {
                string rel = lib.Downloads.Artifact.Path ?? lib.Coordinate.RelativePath;
                toFetch.Add((lib.Downloads.Artifact, rel));
            }
            if (lib.Natives is not null && lib.Downloads?.Classifiers is not null &&
                lib.Natives.TryGetValue(ruleCtx.OsName, out string? classifierKey) &&
                lib.Downloads.Classifiers.TryGetValue(classifierKey, out Downloadable? native))
            {
                string rel = native.Path ?? $"{lib.Coordinate.RelativePath}-{classifierKey}";
                toFetch.Add((native, rel));
            }
        }
        if (toFetch.Count == 0) return;
        var mirroredLibs = mirror is null
            ? toFetch
            : toFetch.Select(t => (WithMirror(t.File, mirror), t.RelativePath)).ToList();
        await downloader.DownloadBatchAsync(mirroredLibs, mc.LibrariesDir, maxConcurrency: libConcurrency, cancel, null, ct);
    }

    private async Task DownloadAssetsWithAsync(VersionInfo info, MinecraftDirectory mc,
        DownloadCancel? cancel, Downloader downloader, int assetConcurrency, string? mirror, CancellationToken ct)
    {
        AssetIndexRef? indexRef = info.AssetIndex;
        if (indexRef is null) return;
        string indexPath = mc.AssetIndexPath(indexRef.Id);
        if (!File.Exists(indexPath))
        {
            Directory.CreateDirectory(mc.AssetIndexesDir);
            string indexUrl = mirror is null ? indexRef.Url : MirrorUrlRewriter.Rewrite(indexRef.Url, mirror);
            string indexJson = await _http.GetStringAsync(indexUrl, ct);
            await File.WriteAllTextAsync(indexPath, indexJson, ct);
        }
        string json = await File.ReadAllTextAsync(indexPath, ct);
        AssetIndex? index = System.Text.Json.JsonSerializer.Deserialize<AssetIndex>(json, JsonOptions.Default);
        if (index?.Objects is null || index.Objects.Count == 0) return;

        var toFetch = new List<(Downloadable File, string RelativePath)>(index.Objects.Count);
        foreach ((_, AssetObject obj) in index.Objects)
        {
            string rel = Path.Combine(obj.Hash[..2], obj.Hash);
            toFetch.Add((new Downloadable
            {
                Sha1 = obj.Hash, Size = obj.Size,
                Url = $"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}",
                Path = rel,
            }, rel));
        }
        var mirroredAssets = mirror is null
            ? toFetch
            : toFetch.Select(t => (WithMirror(t.File, mirror), t.RelativePath)).ToList();
        await downloader.DownloadBatchAsync(mirroredAssets, mc.AssetObjectsDir, maxConcurrency: assetConcurrency, cancel, null, ct);
    }

    private async Task DownloadClientJarAsync(
        VersionInfo info, MinecraftDirectory mc,
        DownloadCancel? cancel, ProgressReporter? progress, string? mirror,
        CancellationToken ct)
    {
        Downloadable? client = info.Downloads?.Client;
        if (client is null)
            throw new InvalidOperationException(
                $"Version '{info.Id}' has no client download (server-only?).");

        Directory.CreateDirectory(mc.VersionDir(info.Id));
        // Route through the configured mirror if one is set (rewrites piston-data.mojang.com etc.).
        Downloadable fetched = mirror is null ? client : WithMirror(client, mirror);
        await _downloader.DownloadAsync(
            fetched, info.Id + ".jar", mc.VersionDir(info.Id),
            cancel, null, ct);
    }

    private async Task DownloadLibrariesAsync(
        VersionInfo info, MinecraftDirectory mc, RuleContext ruleCtx,
        DownloadCancel? cancel, ProgressReporter? progress, int libConcurrency, string? mirror,
        CancellationToken ct)
    {
        var toFetch = new List<(Downloadable File, string RelativePath)>();

        foreach (Library lib in info.Libraries)
        {
            // Skip libraries gated to other OSes/archs.
            if (!RuleEvaluator.IsAllowed(lib.Rules, ruleCtx)) continue;

            // Main artifact (always fetched for current platform).
            if (lib.Downloads?.Artifact is not null)
            {
                string rel = lib.Downloads.Artifact.Path ?? lib.Coordinate.RelativePath;
                toFetch.Add((lib.Downloads.Artifact, rel));
            }

            // Native classifier for the current OS, if any.
            if (lib.Natives is not null && lib.Downloads?.Classifiers is not null)
            {
                if (lib.Natives.TryGetValue(ruleCtx.OsName, out string? classifierKey) &&
                    lib.Downloads.Classifiers.TryGetValue(classifierKey, out Downloadable? native))
                {
                    string rel = native.Path ?? $"{lib.Coordinate.RelativePath}-{classifierKey}";
                    toFetch.Add((native, rel));
                }
            }
        }

        if (toFetch.Count == 0) return;
        _logger.LogInformation("Downloading {Count} libraries…", toFetch.Count);
        // Route through the configured mirror if one is set (rewrites Mojang hosts only).
        var mirroredLibs = mirror is null
            ? toFetch
            : toFetch.Select(t => (WithMirror(t.File, mirror), t.RelativePath)).ToList();
        await _downloader.DownloadBatchAsync(mirroredLibs, mc.LibrariesDir, maxConcurrency: libConcurrency,
            cancel, progress, ct);
    }

    private async Task DownloadAssetsAsync(
        VersionInfo info, MinecraftDirectory mc,
        DownloadCancel? cancel, ProgressReporter? progress, int assetConcurrency, string? mirror,
        CancellationToken ct)
    {
        AssetIndexRef? indexRef = info.AssetIndex;
        if (indexRef is null)
        {
            _logger.LogWarning("Version {Id} has no asset index reference; skipping assets.", info.Id);
            return;
        }

        // Ensure the index JSON is on disk and current.
        string indexPath = mc.AssetIndexPath(indexRef.Id);
        if (!File.Exists(indexPath))
        {
            Directory.CreateDirectory(mc.AssetIndexesDir);
            // The asset index document is served from piston-meta.mojang.com — route through
            // the mirror when one is set so users behind the GFW can fetch it too.
            string indexUrl = mirror is null ? indexRef.Url : MirrorUrlRewriter.Rewrite(indexRef.Url, mirror);
            string indexJson = await _http.GetStringAsync(indexUrl, ct);
            await File.WriteAllTextAsync(indexPath, indexJson, ct);
        }

        // Parse the index to enumerate asset objects.
        string json = await File.ReadAllTextAsync(indexPath, ct);
        AssetIndex? index = System.Text.Json.JsonSerializer.Deserialize<AssetIndex>(json, JsonOptions.Default);
        if (index?.Objects is null || index.Objects.Count == 0) return;

        var toFetch = new List<(Downloadable File, string RelativePath)>(index.Objects.Count);
        foreach ((_, AssetObject obj) in index.Objects)
        {
            // Assets are stored under assets/objects/{hash[0..2]}/{hash} but fetched from
            // resources.download.minecraft.net keyed by the same hash path.
            string rel = Path.Combine(obj.Hash[..2], obj.Hash);
            toFetch.Add((new Downloadable
            {
                Sha1 = obj.Hash,
                Size = obj.Size,
                Url = $"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}",
                Path = rel,
            }, rel));
        }

        _logger.LogInformation("Downloading {Count} asset objects…", toFetch.Count);
        var mirroredAssets = mirror is null
            ? toFetch
            : toFetch.Select(t => (WithMirror(t.File, mirror), t.RelativePath)).ToList();
        await _downloader.DownloadBatchAsync(mirroredAssets, mc.AssetObjectsDir, maxConcurrency: assetConcurrency,
            cancel, progress, ct);
    }

    /// <summary>
    /// Extract native JARs (the platform-specific classifier for each lib with a
    /// <c>natives</c> map) into <c>bin/natives</c>, honoring <c>extract.exclude</c>.
    /// </summary>
    private Task ExtractNativesAsync(
        VersionInfo info, MinecraftDirectory mc, RuleContext ruleCtx, CancellationToken ct)
    {
        Directory.CreateDirectory(mc.NativesDir);

        foreach (Library lib in info.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules, ruleCtx)) continue;
            if (lib.Natives is null || lib.Downloads?.Classifiers is null) continue;
            if (!lib.Natives.TryGetValue(ruleCtx.OsName, out string? classifierKey)) continue;
            if (!lib.Downloads.Classifiers.TryGetValue(classifierKey, out Downloadable? native)) continue;

            string rel = native.Path ?? $"{lib.Coordinate.RelativePath}-{classifierKey}";
            string jarPath = mc.LibraryPath(rel);
            if (!File.Exists(jarPath))
            {
                _logger.LogWarning("Native jar missing on disk, skipping extract: {Path}", jarPath);
                continue;
            }

            HashSet<string>? exclude = lib.Extract?.Exclude?.ToHashSet(StringComparer.OrdinalIgnoreCase);

            using var archive = ZipFile.OpenRead(jarPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (exclude is not null && IsExcluded(entry.FullName, exclude)) continue;

                string dest = Download.ZipSafeExtractor.SafeDestination(mc.NativesDir, entry.FullName);
                string? dir = Path.GetDirectoryName(dest);
                if (dir is not null) Directory.CreateDirectory(dir);

                entry.ExtractToFile(dest, overwrite: true);
            }

            _logger.LogDebug("Extracted natives from {Jar}.", jarPath);
        }

        return Task.CompletedTask;
    }

    private static bool IsExcluded(string path, HashSet<string> exclude)
    {
        // Mojang's exclude patterns are globs like "META-INF/*". Simple prefix/dir match.
        foreach (string pattern in exclude)
        {
            if (pattern.EndsWith('*'))
            {
                string prefix = pattern[..^1];
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Clone a <see cref="Downloadable"/> with its URL rewritten to go through the mirror
    /// (if the URL targets a known Mojang host; otherwise unchanged). Lets us keep the immutable
    /// source model intact while routing the actual bytes through a mirror.</summary>
    private static Downloadable WithMirror(Downloadable src, string mirror)
    {
        string rewritten = MirrorUrlRewriter.Rewrite(src.Url, mirror);
        return new Downloadable { Url = rewritten, Sha1 = src.Sha1, Size = src.Size, Path = src.Path };
    }
}
