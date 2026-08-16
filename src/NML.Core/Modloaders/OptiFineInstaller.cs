using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NML.Core;
using NML.Core.Download;

namespace NML.Core.Modloaders;

/// <summary>
/// OptiFine installer. OptiFine doesn't have a maven repository or JSON API — its versions
/// are listed on optifine.net/download and the installer JARs follow a fixed URL pattern.
/// This installer fetches the version list from the OptiFine downloads page, lets the user
/// pick a version, downloads the installer JAR, and runs it with Java (the installer patches
/// the vanilla jar + writes a version.json with inheritsFrom).
/// </summary>
public sealed class OptiFineInstaller
{
    private const string DownloadsPage = "https://optifine.net/download";
    private const string InstallerBaseUrl = "https://optifine.net/downloadx";

    private readonly IHttpFetcher _http;
    private readonly ILogger<OptiFineInstaller> _logger;

    public OptiFineInstaller(IHttpFetcher http, ILogger<OptiFineInstaller> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Fetch available OptiFine versions for a given Minecraft version. Parses the OptiFine
    /// downloads page HTML (or a community mirror's JSON) for version entries matching the
    /// game version.
    /// </summary>
    public async Task<IReadOnlyList<OptiFineVersion>> ListVersionsAsync(
        string gameVersion, CancellationToken ct = default)
    {
        // OptiFine has a simple JSON-like API at optifine.net/changelog?f=OptiFine_{mc}_{type}
        // But the most reliable approach is the community-maintained BMCLAPI mirror which
        // exposes optifine versions as JSON. We use that.
        const string BmclApi = "https://bmclapi2.bangbang93.com/optifine/{mc}";
        string url = BmclApi.Replace("{mc}", gameVersion);

        try
        {
            string json = await _http.GetStringAsync(url, ct);
            return ParseBmclVersions(json, gameVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch OptiFine versions for {Mc}.", gameVersion);
            return Array.Empty<OptiFineVersion>();
        }
    }

    /// <summary>Parse BMCLAPI's OptiFine version list JSON.
    /// Real entry shape: { "mcversion":"1.12.2", "type":"HD_U", "patch":"C5",
    /// "filename":"OptiFine_1.12.2_HD_U_C5.jar" } — note <c>type</c> is the product line
    /// ("HD_U") and <c>patch</c> is the short version ("C5").</summary>
    internal static IReadOnlyList<OptiFineVersion> ParseBmclVersions(string json, string gameVersion)
    {
        var versions = new List<OptiFineVersion>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                string patch = entry.TryGetProperty("patch", out var p) ? p.GetString() ?? "" : "";
                string type = entry.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                string filename = entry.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(patch)) continue; // patch is the distinguishing short version

                versions.Add(new OptiFineVersion
                {
                    GameVersion = gameVersion,
                    Type = patch,                    // e.g. "C5" — used in profile id + display
                    Patch = type,                    // e.g. "HD_U" (product line)
                    FileName = filename,             // e.g. "OptiFine_1.12.2_HD_U_C5.jar"
                    Display = $"OptiFine {gameVersion} {type} {patch}",
                });
            }
        }
        catch { /* malformed JSON */ }
        return versions;
    }

    /// <summary>
    /// Install OptiFine for <paramref name="gameVersion"/> using <paramref name="type"/> (e.g. "C6").
    /// Downloads the installer JAR and runs it with Java to patch the vanilla jar.
    /// </summary>
    public async Task<string> InstallAsync(
        string gameVersion,
        string type,
        string patch,
        string installerCacheDir,
        string javaExecutable,
        MinecraftDirectory mc,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Installing OptiFine {Type} for {Game}…", type, gameVersion);

        // Build the profile id: OptiFine uses "OptiFine_{mc}_{type}" as the version id.
        string profileId = $"OptiFine_{gameVersion}_{type}";

        // Resolve the exact installer file name for this build. The caller passes the short
        // version (e.g. "C5"); find the matching entry's filename from the version list so the
        // download URL is exact (BMCLAPI serves /optifine/{mc}/{filename}).
        string fileName = $"OptiFine_{gameVersion}_HD_U_{type}.jar"; // conservative fallback
        try
        {
            var all = await ListVersionsAsync(gameVersion, ct);
            var match = all.FirstOrDefault(v => string.Equals(v.Type, type, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !string.IsNullOrEmpty(match.FileName))
                fileName = match.FileName;
        }
        catch { /* list lookup is best-effort; fall back to the constructed name */ }

        // BMCLAPI mirrors the OptiFine installer JARs. Verified path shape: /optifine/{filename}
        // (NO mc-version segment — /optifine/{mc}/{filename} is a 404). optifine.net/downloadx
        // also serves the jar but is unreliable from CN; BMCLAPI first, official as fallback.
        string bmclUrl = $"https://bmclapi2.bangbang93.com/optifine/{fileName}";
        string officialUrl = $"{InstallerBaseUrl}?f={fileName}";
        Directory.CreateDirectory(installerCacheDir);
        string installerJar = Path.Combine(installerCacheDir, fileName);

        if (!File.Exists(installerJar))
        {
            _logger.LogInformation("Downloading OptiFine installer from {Url}…", bmclUrl);
            byte[] bytes;
            try
            {
                bytes = await _http.GetByteArrayAsync(bmclUrl, ct);
            }
            catch
            {
                _logger.LogWarning("BMCLAPI download failed; falling back to optifine.net for {File}.", fileName);
                bytes = await _http.GetByteArrayAsync(officialUrl, ct);
            }
            await File.WriteAllBytesAsync(installerJar, bytes, ct);
        }

        // Run the installer with Java. The OptiFine installer accepts command-line args:
        //   java -jar OptiFine.jar --install.path={mc.root}
        var psi = new System.Diagnostics.ProcessStartInfo(javaExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = installerCacheDir,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerJar);
        psi.ArgumentList.Add($"--install.path={mc.Root}");

        _logger.LogInformation("Running OptiFine installer…");
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) throw new InvalidOperationException("Failed to start Java for OptiFine installer.");
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            string stderr = await process.StandardError.ReadToEndAsync(ct);
            _logger.LogError("OptiFine installer exited with code {Code}: {Stderr}", process.ExitCode, stderr);
            throw new InvalidOperationException($"OptiFine installer failed (exit {process.ExitCode}): {stderr}");
        }

        _logger.LogInformation("OptiFine installed as profile {Id}.", profileId);
        return profileId;
    }
}

public sealed class OptiFineVersion
{
    public string GameVersion { get; init; } = string.Empty;
    /// <summary>Short version, e.g. "C5" (BMCLAPI's <c>patch</c> field).</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>Product line, e.g. "HD_U" (BMCLAPI's <c>type</c> field).</summary>
    public string Patch { get; init; } = string.Empty;
    /// <summary>Exact installer file name, e.g. "OptiFine_1.12.2_HD_U_C5.jar".</summary>
    public string FileName { get; init; } = string.Empty;
    public string Display { get; init; } = string.Empty;
}
