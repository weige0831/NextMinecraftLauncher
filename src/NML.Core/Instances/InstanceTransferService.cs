using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NML.Core;
using NML.Core.Download;
using NML.Core.Instances;

namespace NML.Core.Instances;

/// <summary>
/// Selectable contents for a deep instance export. The base dirs (mods/config/resourcepacks/
/// shaderpacks) are always bundled so a modded instance remains playable; the optional flags
/// layer in user content that turns a modpack into a faithful "everything" snapshot.
/// </summary>
public sealed record ModpackExportOptions
{
    /// <summary>Include the <c>saves/</c> folder (world single-player worlds). Off by default —
    /// worlds are large and usually personal.</summary>
    public bool IncludeSaves { get; init; }

    /// <summary>Include the <c>screenshots/</c> folder. Off by default.</summary>
    public bool IncludeScreenshots { get; init; }

    /// <summary>Include <c>options.txt</c>, <c>servers.dat</c>, <c>servers.dat_old</c>,
    /// <c>optionsof.txt</c> — the per-instance client settings. Off by default.</summary>
    public bool IncludeClientSettings { get; init; }

    /// <summary>Include the <c>logs/</c> folder (latest.log + older logs). Off by default.</summary>
    public bool IncludeLogs { get; init; }

    /// <summary>Default export: mods/config/resourcepacks/shaderpacks only (the existing behavior).</summary>
    public static ModpackExportOptions Default { get; } = new();
}

/// <summary>
/// Exports and imports launcher instances as portable .zip archives. An export bundle contains
/// an <c>instance.json</c> (the Instance metadata) plus selected game-dir subfolders
/// (<c>mods/</c>, <c>config/</c>, <c>resourcepacks/</c>) so the instance can be recreated on
/// another machine or shared with another user. Import reverses the process into a new instance.
/// </summary>
public sealed class InstanceTransferService
{
    private readonly InstanceStore _instances;
    private readonly ILogger<InstanceTransferService> _logger;

    private static readonly string[] ExportDirs = { "mods", "config", "resourcepacks", "shaderpacks" };

    /// <summary>Per-instance client-settings files layered in when IncludeClientSettings is on.</summary>
    private static readonly string[] ClientSettingFiles =
        { "options.txt", "servers.dat", "servers.dat_old", "optionsof.txt", "realms_persistence.json" };

    public InstanceTransferService(InstanceStore instances, ILogger<InstanceTransferService> logger)
    {
        _instances = instances;
        _logger = logger;
    }

    /// <summary>
    /// Export an instance to a .zip at <paramref name="outputPath"/>. The zip contains
    /// <c>instance.json</c> + the contents of the instance's mods/config/etc. dirs. Equivalent to
    /// <see cref="ExportDeep"/> with default options (no worlds/screenshots/settings).
    /// </summary>
    public void Export(Instance instance, string outputPath)
        => ExportDeep(instance, outputPath, ModpackExportOptions.Default);

    /// <summary>
    /// Export an instance with selectable deep contents. Beyond the always-bundled mod dirs, the
    /// <paramref name="options"/> flags add worlds, screenshots, client-settings files, and logs,
    /// so a checked-everything export reproduces the instance faithfully on another machine.
    /// </summary>
    public void ExportDeep(Instance instance, string outputPath, ModpackExportOptions options)
    {
        if (!File.Exists(outputPath) && Directory.Exists(Path.GetDirectoryName(outputPath)) == false)
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string gameDir = _instances.GameDirFor(instance.Name);
        _logger.LogInformation("Exporting instance {Name} to {Path} (deep: saves={Saves}, shots={Shots}, settings={Set}, logs={Logs})…",
            instance.Name, outputPath, options.IncludeSaves, options.IncludeScreenshots, options.IncludeClientSettings, options.IncludeLogs);

        using (var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            int entryCount = 0;
            // Write instance.json into the archive.
            string json = JsonSerializer.Serialize(instance, new JsonSerializerOptions { WriteIndented = true });
            AddTextEntry(zip, "instance.json", json);
            entryCount++;

            // Always-bundled mod/config dirs.
            foreach (string sub in ExportDirs)
                entryCount += AddDir(zip, gameDir, sub);

            // Optional user-content dirs.
            if (options.IncludeSaves) entryCount += AddDir(zip, gameDir, "saves");
            if (options.IncludeScreenshots) entryCount += AddDir(zip, gameDir, "screenshots");
            if (options.IncludeLogs) entryCount += AddDir(zip, gameDir, "logs");

            // Optional per-instance client-settings files (top-level, not a folder).
            if (options.IncludeClientSettings)
            {
                foreach (string fileName in ClientSettingFiles)
                {
                    string file = Path.Combine(gameDir, fileName);
                    if (File.Exists(file))
                    {
                        zip.CreateEntryFromFile(file, fileName, CompressionLevel.Optimal);
                        entryCount++;
                    }
                }
            }

            // Note: ZipArchive.Entries is unreadable in Create mode, so we count as we add.
            _logger.LogInformation("Exported {Name} ({Count} entries).", instance.Name, entryCount);
        }
    }

    /// <summary>Recursively add a game-dir subfolder into the zip under its own name.
    /// Returns the number of files added. Entry names use forward slashes per the ZIP spec
    /// (Path.Combine emits backslashes on Windows, which break cross-platform extraction).</summary>
    private static int AddDir(ZipArchive zip, string gameDir, string sub)
    {
        string dir = Path.Combine(gameDir, sub);
        if (!Directory.Exists(dir)) return 0;
        int n = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.Combine(sub, Path.GetRelativePath(dir, file)).Replace('\\', '/');
            zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Import an instance from a .zip bundle. Creates a new instance with the archived name
    /// + metadata, extracts the bundled mods/config/etc. into the new game dir.
    /// </summary>
    public Instance Import(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Instance bundle not found.", zipPath);

        _logger.LogInformation("Importing instance from {Path}…", zipPath);

        using var archive = ZipFile.OpenRead(zipPath);

        // Read instance.json from the archive.
        var entry = archive.GetEntry("instance.json")
            ?? throw new InvalidDataException("Bundle has no instance.json.");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();
        Instance? instance = JsonSerializer.Deserialize<Instance>(json)
            ?? throw new InvalidDataException("instance.json is invalid.");

        // Ensure a unique name if one with the same name already exists.
        string name = instance.Name;
        int suffix = 1;
        var existing = _instances.LoadAll();
        while (existing.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{instance.Name} ({suffix++})";
        }
        instance.Name = name;

        // Create the game dir and extract bundled subfolders.
        string gameDir = _instances.GameDirFor(name);
        Directory.CreateDirectory(gameDir);

        foreach (ZipArchiveEntry e in archive.Entries)
        {
            if (e.FullName == "instance.json") continue;
            // Zip Slip guard: reject entries that escape the instance's game dir.
            ZipSafeExtractor.ExtractEntry(e, gameDir);
        }

        // Persist the new instance.
        _instances.Add(instance);
        _logger.LogInformation("Imported instance {Name}.", name);
        return instance;
    }

    /// <summary>Helper to write a text entry into a zip archive.</summary>
    private static void AddTextEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var s = entry.Open();
        using var w = new StreamWriter(s);
        w.Write(content);
    }
}
