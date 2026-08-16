using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NML.Core;
using NML.Core.Download;
using NML.Core.Models;

namespace NML.Core.Modpacks;

/// <summary>
/// Installs a modpack into a new isolated instance. Supports Modrinth's <c>.mrpack</c>
/// format (a zip with <c>modrinth.index.json</c> + <c>overrides/</c>) and the CurseForge
/// format (a zip with <c>manifest.json</c> + <c>overrides/</c>). After unpacking the
/// overrides (config files, saves, etc.) it queues the mod files for download.
/// </summary>
public sealed class ModpackInstaller
{
    private readonly IHttpFetcher _http;
    private readonly Downloader _downloader;
    private readonly ILogger<ModpackInstaller> _logger;
    private readonly ICurseForgeFileResolver? _curseForgeResolver;

    public ModpackInstaller(
        IHttpFetcher http,
        Downloader downloader,
        ILogger<ModpackInstaller> logger,
        ICurseForgeFileResolver? curseForgeResolver = null)
    {
        _http = http;
        _downloader = downloader;
        _logger = logger;
        _curseForgeResolver = curseForgeResolver;
    }

    /// <summary>
    /// Install a modpack from a downloaded archive path into a new isolated game dir.
    /// Returns the instance name to create.
    /// </summary>
    public async Task<string> InstallAsync(
        string archivePath,
        string instanceName,
        MinecraftDirectory mc,
        DownloadCancel? cancel = null,
        ProgressReporter? progress = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Installing modpack {Archive}…", Path.GetFileName(archivePath));

        Directory.CreateDirectory(mc.Root);
        using var archive = ZipFile.OpenRead(archivePath);

        // Detect format: Modrinth has modrinth.index.json, CurseForge has manifest.json.
        ZipArchiveEntry? modrinthEntry = archive.GetEntry("modrinth.index.json");
        ZipArchiveEntry? curseEntry = archive.GetEntry("manifest.json");

        if (modrinthEntry is not null)
            await InstallModrinthAsync(archive, modrinthEntry, mc, cancel, progress, ct);
        else if (curseEntry is not null)
            await InstallCurseForgeAsync(archive, curseEntry, mc, cancel, progress, ct);
        else
            throw new InvalidDataException(
                "Unrecognized modpack format (no modrinth.index.json or manifest.json).");

        // Extract the overrides/ folder over the game dir for both formats.
        ExtractOverrides(archive, mc.Root);

        _logger.LogInformation("Modpack installed into {Dir}.", mc.Root);
        return instanceName;
    }

    private async Task InstallModrinthAsync(
        ZipArchive archive, ZipArchiveEntry indexEntry, MinecraftDirectory mc,
        DownloadCancel? cancel, ProgressReporter? progress, CancellationToken ct)
    {
        using var stream = indexEntry.Open();
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        JsonElement root = doc.RootElement;

        // modrinth.index.json: { "game": "1.20.1", "dependencies": { "fabric-loader": "...", "minecraft": "..." }, "files": [...] }
        string gameVersion = root.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.String
            ? g.GetString() ?? string.Empty : string.Empty;

        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            var toFetch = new List<(Downloadable File, string RelativePath)>();
            foreach (var f in files.EnumerateArray())
            {
                string path = f.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                var downloads = f.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Array
                    ? dl.EnumerateArray().FirstOrDefault().GetString() ?? "" : "";
                var hashes = f.TryGetProperty("hashes", out var h) ? h : default;
                string sha1 = hashes.ValueKind == JsonValueKind.Object
                              && hashes.TryGetProperty("sha1", out var s1) ? s1.GetString() ?? "" : "";
                long size = f.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long sv) ? sv : 0;

                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(downloads))
                {
                    // Files are stored under the game dir at the given path (e.g. "mods/sodium.jar").
                    toFetch.Add((new Downloadable { Url = downloads, Sha1 = sha1, Size = size, Path = path }, path));
                }
            }

            if (toFetch.Count > 0)
            {
                _logger.LogInformation("Downloading {Count} modpack files…", toFetch.Count);
                await _downloader.DownloadBatchAsync(toFetch, mc.Root, maxConcurrency: 8, cancel, progress, ct);
            }
        }
    }

    private async Task InstallCurseForgeAsync(
        ZipArchive archive, ZipArchiveEntry manifestEntry, MinecraftDirectory mc,
        DownloadCancel? cancel, ProgressReporter? progress, CancellationToken ct)
    {
        using var stream = manifestEntry.Open();
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        JsonElement root = doc.RootElement;

        // manifest.json: { "minecraft": { "version": "1.20.1", "modLoaders": [...] },
        //                  "files": [ { "projectID":.., "fileID":.., "required": true } ] }
        if (!root.TryGetProperty("files", out var files) || files.GetArrayLength() == 0)
        {
            _logger.LogInformation("CurseForge modpack has no mod files; only overrides will be extracted.");
            return;
        }

        // Collect (projectID, fileID) pairs, honoring the "required" flag (skip optional).
        var ids = new List<(int ProjectId, int FileId)>();
        foreach (var f in files.EnumerateArray())
        {
            bool required = !f.TryGetProperty("required", out var r) || r.ValueKind != JsonValueKind.False || r.GetBoolean();
            if (!required) continue;
            int pid = f.TryGetProperty("projectID", out var pe) ? pe.GetInt32() : 0;
            int fid = f.TryGetProperty("fileID", out var fe) ? fe.GetInt32() : 0;
            if (pid != 0 && fid != 0) ids.Add((pid, fid));
        }

        if (ids.Count == 0) return;

        // Without a CurseForge API key (no resolver injected), we cannot resolve the mod URLs —
        // log clearly so the user knows what to do, and only overrides get extracted.
        if (_curseForgeResolver is null)
        {
            _logger.LogWarning(
                "CurseForge modpack has {Count} mod files but no CurseForge API key is configured. " +
                "Add a CurseForge API key in Settings to download them; overrides still extracted.",
                ids.Count);
            return;
        }

        _logger.LogInformation("Resolving {Count} CurseForge mod files via the API…", ids.Count);
        IReadOnlyList<CurseForgeResolvedFile> resolved = await _curseForgeResolver.ResolveAsync(ids, ct);

        // Queue each resolved mod for download into the instance's mods/ directory.
        var toFetch = new List<(Downloadable File, string RelativePath)>();
        foreach (CurseForgeResolvedFile f in resolved)
        {
            if (string.IsNullOrEmpty(f.DownloadUrl)) continue;
            // CurseForge mod files go under mods/<filename>.jar.
            string rel = Path.Combine("mods", f.FileName);
            toFetch.Add((new Downloadable
            {
                Url = f.DownloadUrl,
                Sha1 = f.Sha1 ?? string.Empty,
                Size = f.Size,
                Path = rel,
            }, rel));
        }

        if (toFetch.Count > 0)
        {
            _logger.LogInformation("Downloading {Count} CurseForge mod files…", toFetch.Count);
            await _downloader.DownloadBatchAsync(toFetch, mc.Root, maxConcurrency: 8,
                cancel, progress, ct);
        }

        if (resolved.Count < ids.Count)
        {
            _logger.LogWarning("{Missing} of {Total} CurseForge files could not be resolved.",
                ids.Count - resolved.Count, ids.Count);
        }
    }

    /// <summary>Extract every entry under <c>overrides/</c> (or <c>client-overrides/</c>) into the game dir.</summary>
    private static void ExtractOverrides(ZipArchive archive, string gameDir)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string? prefix = entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)
                ? "overrides/"
                : entry.FullName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase)
                    ? "client-overrides/"
                    : null;

            if (prefix is null) continue;
            string rel = entry.FullName[prefix.Length..];
            if (string.IsNullOrEmpty(rel) || rel.EndsWith('/')) continue;

            // Zip Slip guard: overrides paths come from a remote modpack zip — reject traversal.
            ZipSafeExtractor.ExtractEntry(entry, gameDir, rel);
        }
    }
}