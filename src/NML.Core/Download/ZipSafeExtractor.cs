using System.IO.Compression;

namespace NML.Core.Download;

/// <summary>
/// Guards archive extraction against Zip Slip (path traversal). Every zip entry whose full name
/// escapes the extraction root — via <c>../</c> segments, an absolute/rooted path (Windows drive
/// letter or UNC), or a null/empty name — is rejected by throwing <see cref="IOException"/>.
/// All launcher extraction sites must resolve entry destinations through
/// <see cref="SafeDestination"/> instead of a raw Path.Combine.
/// </summary>
public static class ZipSafeExtractor
{
    /// <summary>
    /// Resolve an archive entry name to a destination under <paramref name="root"/>, throwing on
    /// any traversal attempt. Returns the safe absolute destination path.
    /// </summary>
    public static string SafeDestination(string root, string entryFullName)
    {
        if (string.IsNullOrWhiteSpace(entryFullName))
            throw new IOException("Archive contains an entry with an empty name.");

        // Normalize separators; reject rooted names (C:\ or / or \\server\share) outright —
        // Path.Combine would return them verbatim, escaping the root entirely.
        string normalized = entryFullName.Replace('\\', '/');
        if (Path.IsPathRooted(entryFullName) || normalized.StartsWith('/'))
            throw new IOException($"Archive entry '{entryFullName}' is an absolute path (Zip Slip).");

        string rootFull = Path.GetFullPath(root);
        // OrdinalIgnoreCase: on Windows paths are case-insensitive; harmless on Unix.
        string destFull = Path.GetFullPath(Path.Combine(rootFull, normalized));
        string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!destFull.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(destFull, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Archive entry '{entryFullName}' escapes the extraction root (Zip Slip).");
        }
        return destFull;
    }

    /// <summary>Extract one entry to a safe destination under <paramref name="root"/> (overwrites).</summary>
    public static void ExtractEntry(ZipArchiveEntry entry, string root)
        => ExtractEntry(entry, root, entry.FullName);

    /// <summary>
    /// Extract one entry under a RELATIVE name derived from its archive path (e.g. an
    /// <c>overrides/</c> prefix stripped by the modpack installer). The relative name is
    /// traversal-checked the same way; use when the destination name differs from FullName.
    /// </summary>
    public static void ExtractEntry(ZipArchiveEntry entry, string root, string relativeName)
    {
        string dest = SafeDestination(root, relativeName);
        string? dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        entry.ExtractToFile(dest, overwrite: true);
    }

    /// <summary>Extract every entry safely under <paramref name="root"/>.</summary>
    public static void ExtractAll(ZipArchive archive, string root)
    {
        Directory.CreateDirectory(root);
        foreach (ZipArchiveEntry entry in archive.Entries)
            ExtractEntry(entry, root);
    }

    /// <summary>
    /// Validate a RELATIVE PATH sourced from remote data (Modrinth/CurseForge file names, .mrpack
    /// paths, mod update targets) before it is combined into a target directory. Rejects rooted
    /// paths and any traversal above the base. Returns the sanitized relative path.
    /// </summary>
    public static string SafeRelativePath(string baseDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new IOException("Remote-provided file path is empty.");
        return SafeDestination(baseDir, relativePath)
            .Substring(Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar).Length)
            .TrimStart(Path.DirectorySeparatorChar);
    }
}
