using NML.Core.Download;

namespace NML.Core;

/// <summary>
/// Read-only access to a Minecraft instance's user content: saves, screenshots,
/// resource packs and (for modded instances) the mods folder. Used by the management UI.
/// </summary>
public sealed class GameContentBrowser
{
    private readonly MinecraftDirectory _mc;

    public GameContentBrowser(MinecraftDirectory mc) => _mc = mc;

    /// <summary>List saved worlds (folders under <c>saves/</c>) with size and last-played time.</summary>
    public IReadOnlyList<GameSave> ListSaves()
    {
        string dir = Path.Combine(_mc.Root, "saves");
        return ListEntries(dir, includeExtensions: null, map: (name, full) =>
        {
            var fi = new DirectoryInfo(full);
            return new GameSave
            {
                Name = name,
                Path = full,
                SizeBytes = DirSize(full),
                LastModified = fi.LastWriteTimeUtc,
                // Enrich: the in-world display name (falls back to folder name) + preview icon.
                DisplayName = WorldMetadataReader.ReadLevelName(full) ?? name,
                PreviewIconPath = WorldMetadataReader.ReadIconPath(full),
            };
        });
    }

    /// <summary>List screenshots (PNG files under <c>screenshots/</c>).</summary>
    public IReadOnlyList<GameFile> ListScreenshots()
    {
        string dir = Path.Combine(_mc.Root, "screenshots");
        return ListEntries(dir, includeExtensions: new[] { ".png", ".jpg" }, map: (name, full) =>
        {
            var fi = new FileInfo(full);
            return new GameFile { Name = name, Path = full, SizeBytes = fi.Length, LastModified = fi.LastWriteTimeUtc };
        }).Cast<GameFile>().ToList();
    }

    /// <summary>List installed resource packs (zip files under <c>resourcepacks/</c>).</summary>
    public IReadOnlyList<GameFile> ListResourcePacks()
    {
        string dir = Path.Combine(_mc.Root, "resourcepacks");
        return ListEntries(dir, includeExtensions: new[] { ".zip" }, map: (name, full) =>
        {
            var fi = new FileInfo(full);
            return new GameFile { Name = name, Path = full, SizeBytes = fi.Length, LastModified = fi.LastWriteTimeUtc };
        }).Cast<GameFile>().ToList();
    }

    /// <summary>List installed mods (jar files under <c>mods/</c>).</summary>
    public IReadOnlyList<GameFile> ListMods()
    {
        string dir = Path.Combine(_mc.Root, "mods");
        return ListEntries(dir, includeExtensions: new[] { ".jar", ".disabled" }, map: (name, full) =>
        {
            var fi = new FileInfo(full);
            return new GameFile { Name = name, Path = full, SizeBytes = fi.Length, LastModified = fi.LastWriteTimeUtc };
        }).Cast<GameFile>().ToList();
    }

    /// <summary>Toggle a mod between enabled (.jar) and disabled (.jar.disabled).</summary>
    public void ToggleMod(string modPath)
    {
        if (modPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            string enabled = modPath[..^".disabled".Length];
            if (File.Exists(enabled)) throw new IOException("An enabled mod with that name already exists.");
            File.Move(modPath, enabled);
        }
        else
        {
            string disabled = modPath + ".disabled";
            File.Move(modPath, disabled);
        }
    }

    /// <summary>Disable all mods (rename *.jar → *.jar.disabled). Returns the count affected.</summary>
    public int DisableAllMods()
    {
        string dir = Path.Combine(_mc.Root, "mods");
        if (!Directory.Exists(dir)) return 0;
        int count = 0;
        foreach (string jar in Directory.EnumerateFiles(dir, "*.jar"))
        {
            string disabled = jar + ".disabled";
            if (!File.Exists(disabled)) { File.Move(jar, disabled); count++; }
        }
        return count;
    }

    /// <summary>Enable all disabled mods (rename *.jar.disabled → *.jar). Returns the count affected.</summary>
    public int EnableAllMods()
    {
        string dir = Path.Combine(_mc.Root, "mods");
        if (!Directory.Exists(dir)) return 0;
        int count = 0;
        foreach (string disabled in Directory.EnumerateFiles(dir, "*.jar.disabled"))
        {
            string enabled = disabled[..^".disabled".Length];
            if (!File.Exists(enabled)) { File.Move(disabled, enabled); count++; }
        }
        return count;
    }

    private static IReadOnlyList<T> ListEntries<T>(
        string dir, string[]? includeExtensions, Func<string, string, T> map)
    {
        if (!Directory.Exists(dir)) return Array.Empty<T>();
        var list = new List<T>();

        IEnumerable<string> entries = Directory.EnumerateFileSystemEntries(dir);
        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            string ext = Path.GetExtension(entry);

            // When filtering by extension, skip folders and non-matching files.
            if (includeExtensions is not null)
            {
                if (Directory.Exists(entry)) continue;
                if (!includeExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase))) continue;
            }
            else
            {
                // For save-listing we only want directories.
                if (!Directory.Exists(entry)) continue;
                // Skip Minecraft's own metadata folders.
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            }

            list.Add(map(name, entry));
        }
        return list;
    }

    private static long DirSize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                            .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    /// <summary>Backup a world save folder into a timestamped .zip in a backups/ directory.</summary>
    public string BackupWorld(string worldPath)
    {
        if (!Directory.Exists(worldPath))
            throw new DirectoryNotFoundException($"World not found: {worldPath}");

        string name = Path.GetFileName(worldPath);
        string backupDir = Path.Combine(_mc.Root, "backups");
        Directory.CreateDirectory(backupDir);
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string zipPath = Path.Combine(backupDir, $"{name}-{stamp}.zip");

        System.IO.Compression.ZipFile.CreateFromDirectory(worldPath, zipPath,
            System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
        return zipPath;
    }

    /// <summary>Enumerate every <c>{world}-{timestamp}.zip</c> in the instance's backups/ folder,
    /// parsed into <see cref="BackupInfo"/> records. Newest first. Empty when none exist.</summary>
    public IReadOnlyList<BackupInfo> ListBackups()
    {
        string backupDir = Path.Combine(_mc.Root, "backups");
        if (!Directory.Exists(backupDir)) return Array.Empty<BackupInfo>();
        var result = new List<BackupInfo>();
        foreach (string zip in Directory.EnumerateFiles(backupDir, "*.zip"))
        {
            var info = BackupInfo.FromPath(zip);
            if (info is not null) result.Add(info);
        }
        // Newest backup first.
        result.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return result;
    }

    /// <summary>
    /// Restore a world from a backup zip: extracts it back over the matching <c>saves/{world}</c>
    /// folder, replacing the current contents. Creates the folder if it no longer exists. Throws
    /// if the backup zip is missing. The caller should confirm since this overwrites live data.
    /// </summary>
    public string RestoreWorld(string backupZipPath)
    {
        if (!File.Exists(backupZipPath))
            throw new FileNotFoundException("Backup not found.", backupZipPath);

        // Derive the world name from the backup filename: "{world}-{stamp}.zip" → "{world}".
        string worldName = BackupInfo.WorldNameFromFileName(Path.GetFileName(backupZipPath));
        string savesDir = Path.Combine(_mc.Root, "saves");
        string worldDir = Path.Combine(savesDir, worldName);

        // Clear the existing world folder so the restore is exact (no stale files left behind).
        if (Directory.Exists(worldDir))
            Directory.Delete(worldDir, recursive: true);
        Directory.CreateDirectory(worldDir);

        System.IO.Compression.ZipFile.ExtractToDirectory(backupZipPath, worldDir, overwriteFiles: true);
        return worldDir;
    }

    /// <summary>
    /// Restore a world from a backup zip with live progress + cancellation — the entry-by-entry
    /// equivalent of <see cref="RestoreWorld"/>, for large multi-GB worlds where a fire-and-forget
    /// restore leaves the user staring at a frozen UI. Reports <c>(extractedBytes, totalBytes)</c>
    /// as each entry is written; throws <see cref="OperationCanceledException"/> if cancelled (the
    /// half-extracted folder is left in place — the caller can re-run to finish, matching the
    /// idempotent extract-with-overwrite behavior).
    /// </summary>
    public async Task<string> RestoreWorldAsync(
        string backupZipPath,
        IProgress<(long extractedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(backupZipPath))
            throw new FileNotFoundException("Backup not found.", backupZipPath);

        string worldName = BackupInfo.WorldNameFromFileName(Path.GetFileName(backupZipPath));
        string savesDir = Path.Combine(_mc.Root, "saves");
        string worldDir = Path.Combine(savesDir, worldName);

        // Clear the existing world folder so the restore is exact (no stale files left behind).
        if (Directory.Exists(worldDir))
            Directory.Delete(worldDir, recursive: true);
        Directory.CreateDirectory(worldDir);

        // Open the archive and compute the total uncompressed size once up front (cheap; entries
        // carry their Length in the central directory).
        using var archive = System.IO.Compression.ZipFile.OpenRead(backupZipPath);
        long totalBytes = archive.Entries.Sum(e => e.Length);
        long extractedBytes = 0;

        // Buffer for stream copying; 64 KiB balances throughput against allocation pressure.
        byte[] buffer = new byte[64 * 1024];

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // Zip Slip guard: a tampered backup must not escape saves/{world}.
            string dest = ZipSafeExtractor.SafeDestination(worldDir, entry.FullName);
            // Directory entries: create and continue (Length is 0 for them).
            if (entry.Length == 0 && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            string? parent = Path.GetDirectoryName(dest);
            if (parent is not null) Directory.CreateDirectory(parent);

            // Extract with overwrite, copying through the buffer so we can report + cancel mid-file.
            await using (var es = entry.Open())
            await using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: buffer.Length, useAsync: true))
            {
                int read;
                while ((read = await es.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                                     .ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    extractedBytes += read;
                    progress?.Report((extractedBytes, totalBytes));
                }
            }
        }
        return worldDir;
    }

    /// <summary>Delete a backup zip (after the caller confirms).</summary>
    public void DeleteBackup(string backupZipPath)
    {
        if (File.Exists(backupZipPath)) File.Delete(backupZipPath);
    }

    /// <summary>
    /// Keep only the newest <paramref name="keepCount"/> backup zips; delete the rest. Returns the
    /// number pruned. Used by auto-backup to bound on-disk accumulation. keepCount &lt;= 0 is a no-op.
    /// </summary>
    public int PruneOldBackups(int keepCount)
    {
        if (keepCount <= 0) return 0;
        var all = ListBackups(); // already newest-first
        if (all.Count <= keepCount) return 0;
        int pruned = 0;
        foreach (var old in all.Skip(keepCount))
        {
            try { if (File.Exists(old.Path)) { File.Delete(old.Path); pruned++; } }
            catch { /* best-effort; a locked zip shouldn't abort pruning */ }
        }
        return pruned;
    }

    /// <summary>Delete a world save folder (after the caller confirms).</summary>
    public void DeleteWorld(string worldPath)
    {
        if (Directory.Exists(worldPath))
            Directory.Delete(worldPath, recursive: true);
    }

    /// <summary>
    /// Rename a world: edits the <c>Data.LevelName</c> tag in level.dat so the in-game name changes,
    /// AND renames the on-disk save folder so the launcher's save list reflects it. When the desired
    /// folder name is already taken, a numeric suffix is appended so the rename never clobbers an
    /// existing world. Returns the absolute path of the renamed world directory.
    /// </summary>
    /// <remarks>The folder name is sanitized to a filesystem-safe segment (path separators and
    /// reserved chars stripped) and deconflicted; the in-game LevelName is written verbatim.</remarks>
    public string RenameWorld(string worldPath, string newDisplayName)
    {
        if (!Directory.Exists(worldPath))
            throw new DirectoryNotFoundException($"World not found: {worldPath}");
        if (string.IsNullOrWhiteSpace(newDisplayName))
            throw new ArgumentException("New world name must not be empty.", nameof(newDisplayName));

        string savesDir = Path.Combine(_mc.Root, "saves");
        Directory.CreateDirectory(savesDir);

        // First edit the in-game LevelName in level.dat (throws on missing tag → caller reports error).
        NML.Core.Game.WorldSettingsManager.WriteLevelName(worldPath, newDisplayName);

        // Derive a safe folder name. Worlds live under saves/<name>/.
        string safeName = SanitizeFolderName(newDisplayName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "World";
        string targetDir = Path.Combine(savesDir, safeName);
        // Deconflict: never overwrite an existing, different world folder.
        if (!string.Equals(NormalizePath(worldPath), NormalizePath(targetDir), StringComparison.OrdinalIgnoreCase))
        {
            int suffix = 1;
            while (Directory.Exists(targetDir))
            {
                targetDir = Path.Combine(savesDir, $"{safeName} ({suffix++})");
            }
            Directory.Move(worldPath, targetDir);
        }
        return targetDir;
    }

    /// <summary>Strip characters that are illegal in Windows folder names, collapse to a single segment.</summary>
    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (Array.IndexOf(invalid, c) >= 0) continue;
            if (c == '/' || c == '\\') continue; // collapse path separators
            sb.Append(c);
        }
        return sb.ToString().Trim().TrimEnd('.');
    }

    private static string NormalizePath(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Delete a screenshot file.</summary>
    public void DeleteScreenshot(string screenshotPath)
    {
        if (File.Exists(screenshotPath))
            File.Delete(screenshotPath);
    }

    /// <summary>Open a screenshot in the OS default image viewer.</summary>
    public void OpenScreenshot(string screenshotPath)
    {
        if (!File.Exists(screenshotPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(screenshotPath)
            {
                UseShellExecute = true,
            });
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Bundle the given screenshot files into a single timestamped .zip on the desktop, so the
    /// user can share a batch. Missing files are skipped silently; returns the zip path.
    /// Used by the screenshot grid's "export selected" action.
    /// </summary>
    public string ExportScreenshotsToZip(IEnumerable<string> paths, string outputZipPath)
    {
        if (string.IsNullOrEmpty(outputZipPath))
            throw new ArgumentException("Output zip path is required.", nameof(outputZipPath));
        string dir = Path.GetDirectoryName(outputZipPath)!;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var archive = System.IO.Compression.ZipFile.Open(
            outputZipPath, System.IO.Compression.ZipArchiveMode.Create);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string p in paths)
        {
            if (!File.Exists(p)) continue;
            // Entry name = bare filename; de-dupe collisions with a counter. (GetEntry throws in
            // Create mode, so we track used names ourselves.)
            string entryName = Path.GetFileName(p);
            string baseName = Path.GetFileNameWithoutExtension(p);
            string ext = Path.GetExtension(p);
            int n = 1;
            while (usedNames.Contains(entryName))
            {
                entryName = $"{baseName} ({n++}){ext}";
            }
            usedNames.Add(entryName);
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, p, entryName);
        }
        return outputZipPath;
    }

    /// <summary>Delete a resource pack file.</summary>
    public void DeleteResourcePack(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Extract the pack.png thumbnail from a resource pack .zip (returns null if absent).
    /// Resource packs always carry a pack.png at the archive root.
    /// </summary>
    public byte[]? GetResourcePackThumbnail(string zipPath)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("pack.png");
            if (entry is null) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>Read the most recent launch log file (if any) and return its content.</summary>
    public string ReadLatestLog(int maxChars = 50000)
    {
        string logsDir = Path.Combine(_mc.Root, "logs");
        if (!Directory.Exists(logsDir)) return string.Empty;

        var latest = Directory.GetFiles(logsDir, "launch-*.log")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest is null) return string.Empty;

        try
        {
            string content = File.ReadAllText(latest.FullName);
            if (content.Length > maxChars)
                content = "…[earlier lines truncated]…\n" + content[^maxChars..];
            return content;
        }
        catch { return string.Empty; }
    }

    /// <summary>List config files under config/ (common mod-config formats: .toml/.cfg/.json/.txt/.properties).</summary>
    public IReadOnlyList<GameFile> ListConfigFiles()
    {
        string dir = Path.Combine(_mc.Root, "config");
        return ListEntries(dir,
            includeExtensions: new[] { ".toml", ".cfg", ".json", ".txt", ".properties", ".ini", ".conf" },
            map: (name, full) =>
            {
                var fi = new FileInfo(full);
                return new GameFile { Name = name, Path = full, SizeBytes = fi.Length, LastModified = fi.LastWriteTimeUtc };
            }).Cast<GameFile>().ToList();
    }

    /// <summary>Read a config file's text content for editing.</summary>
    public string ReadConfigFile(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        return File.ReadAllText(path);
    }

    /// <summary>Write edited content back to a config file.</summary>
    public void WriteConfigFile(string path, string content)
    {
        File.WriteAllText(path, content);
    }

    /// <summary>Export a world save folder to a .zip at the given output path.</summary>
    public void ExportWorld(string worldPath, string outputPath)
    {
        if (!Directory.Exists(worldPath))
            throw new DirectoryNotFoundException($"World not found: {worldPath}");
        string? dir = Path.GetDirectoryName(outputPath);
        if (dir is not null) Directory.CreateDirectory(dir);
        System.IO.Compression.ZipFile.CreateFromDirectory(worldPath, outputPath,
            System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    /// <summary>Import a world save from a .zip into the saves/ directory.</summary>
    public string ImportWorld(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("World zip not found.", zipPath);

        string savesDir = Path.Combine(_mc.Root, "saves");
        Directory.CreateDirectory(savesDir);

        // Derive a world name from the zip filename, ensure uniqueness.
        string baseName = Path.GetFileNameWithoutExtension(zipPath);
        string worldName = baseName;
        string worldDir = Path.Combine(savesDir, worldName);
        int suffix = 1;
        while (Directory.Exists(worldDir))
        {
            worldName = $"{baseName} ({suffix++})";
            worldDir = Path.Combine(savesDir, worldName);
        }

        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, worldDir, overwriteFiles: true);
        return worldDir;
    }
}

public sealed class GameSave
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTimeOffset LastModified { get; init; }

    /// <summary>The in-world display name read from <c>level.dat</c>'s LevelName tag.
    /// Falls back to the folder <see cref="Name"/> when level.dat is missing/unreadable.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Absolute path to <c>icon.png</c> beside the level.dat, or null when the world
    /// has no custom icon (the UI shows a generated placeholder in that case).</summary>
    public string? PreviewIconPath { get; init; }
}

public sealed class GameFile
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTimeOffset LastModified { get; init; }
}

/// <summary>
/// One world-backup zip: the world name it snapshots, the captured timestamp parsed from the
/// filename, the file size, and the absolute path (passed back to RestoreWorld/DeleteBackup).
/// </summary>
public sealed class BackupInfo
{
    /// <summary>The world this backup snapshots (folder name under saves/).</summary>
    public string WorldName { get; init; } = string.Empty;

    /// <summary>UTC timestamp parsed from the <c>{world}-yyyyMMdd-HHmmss.zip</c> filename.
    /// Falls back to the file's last-write time if the stamp is unparseable.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Backup file size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Absolute path to the backup zip.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Parse a backup filename into a BackupInfo, or null if the path isn't a valid
    /// <c>{world}-yyyyMMdd-HHmmss.zip</c> shape.</summary>
    public static BackupInfo? FromPath(string zipPath)
    {
        if (!File.Exists(zipPath)) return null;
        string fileName = System.IO.Path.GetFileName(zipPath);
        string worldName = WorldNameFromFileName(fileName);
        if (string.IsNullOrEmpty(worldName)) return null;

        var fi = new FileInfo(zipPath);
        // Stamp sits after the final hyphen: "World1-20240101-120000.zip".
        DateTimeOffset ts = fi.LastWriteTimeUtc;
        // Explicit parse: strip ".zip", take the trailing "yyyyMMdd-HHmmss".
        string noExt = fileName[..^4];
        const int stampLen = 15; // "yyyyMMdd-HHmmss"
        if (noExt.Length > stampLen + 1)
        {
            string stamp = noExt[^stampLen..];
            if (DateTimeOffset.TryParseExact(stamp, "yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                ts = parsed.ToUniversalTime();
        }

        return new BackupInfo
        {
            WorldName = worldName,
            Timestamp = ts,
            SizeBytes = fi.Length,
            Path = zipPath,
        };
    }

    /// <summary>Extract the world name from a backup filename by stripping the trailing
    /// <c>-yyyyMMdd-HHmmss.zip</c> stamp. Returns empty if the shape doesn't match.</summary>
    public static string WorldNameFromFileName(string fileName)
    {
        string noExt = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4] : fileName;
        const int stampLen = 16; // "-yyyyMMdd-HHmmss"
        return noExt.Length > stampLen ? noExt[..^stampLen] : noExt;
    }
}

