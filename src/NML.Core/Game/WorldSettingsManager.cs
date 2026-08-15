using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NML.Core.Game;

/// <summary>
/// Reads and writes a world's difficulty + selected gamerules from its level.dat, so the launcher
/// can let the user toggle keepInventory / doDaylightCycle / doMobSpawning / etc. and change the
/// difficulty without launching the game. Uses the same minimal NBT scanner approach as
/// WorldMetadataReader — no full NBT library, just targeted byte-scanning for the tags we need.
/// <para>
/// Difficulty is a Byte tag (0=peaceful, 1=easy, 2=normal, 3=hard). GameRules are a compound of
/// String tags under Data→GameRules, each value is "true"/"false" or a number string.
/// </para>
/// </summary>
public static class WorldSettingsManager
{
    /// <summary>Difficulty names ↔ byte values.</summary>
    public static readonly IReadOnlyDictionary<string, byte> DifficultyValues = new Dictionary<string, byte>
    {
        { "peaceful", 0 }, { "easy", 1 }, { "normal", 2 }, { "hard", 3 },
    };

    /// <summary>Common gamerules the UI exposes as toggles.</summary>
    public static readonly IReadOnlyList<string> ToggleableRules = new[]
    {
        "keepInventory", "doDaylightCycle", "doMobSpawning", "doFireTick",
        "mobGriefing", "doWeatherCycle", "naturalRegeneration", "showDeathMessages",
    };

    /// <summary>GameType names ↔ int values (Mojang's GameType constants, stored as Data.GameType).</summary>
    public static readonly IReadOnlyDictionary<string, int> GameTypeValues = new Dictionary<string, int>
    {
        { "survival", 0 }, { "creative", 1 }, { "adventure", 2 }, { "spectator", 3 },
    };

    /// <summary>Convert a GameType int (0–3) to its name; unknown → "survival".</summary>
    public static string GameTypeName(int value) => value switch
    {
        0 => "survival", 1 => "creative", 2 => "adventure", 3 => "spectator", _ => "survival",
    };

    /// <summary>Convert a GameType name to its int value; unknown → 0 (survival).</summary>
    public static int GameTypeInt(string name)
        => GameTypeValues.TryGetValue((name ?? "").ToLowerInvariant(), out int v) ? v : 0;

    /// <summary>Read the difficulty (as a name), GameType (survival/creative/...), spawn-protection
    /// radius, command-cheat flag, hardcore flag, and the toggleable gamerules from a world dir's
    /// level.dat. Returns defaults when the file is missing or unreadable.</summary>
    public static WorldSettings Read(string worldDir)
    {
        string levelDat = Path.Combine(worldDir, "level.dat");
        if (!File.Exists(levelDat)) return new WorldSettings();

        try
        {
            using var fs = File.OpenRead(levelDat);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            byte[] nbt = ms.ToArray();

            byte diff = FindByteTag(nbt, "Difficulty", (byte)2);
            int gameType = FindIntTag(nbt, "GameType", 0);
            int spawnProtection = FindIntTag(nbt, "SpawnProtection", 16);
            bool allowCommands = FindByteTag(nbt, "allowCommands", (byte)1) != 0;
            bool hardcore = FindByteTag(nbt, "hardcore", (byte)0) != 0;
            var rules = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string rule in ToggleableRules)
            {
                string? value = FindStringTag(nbt, rule);
                if (value is not null) rules[rule] = value;
            }
            return new WorldSettings
            {
                Difficulty = DifficultyName(diff),
                GameType = GameTypeName(gameType),
                SpawnProtectionRadius = spawnProtection,
                AllowCommands = allowCommands,
                Hardcore = hardcore,
                GameRules = rules,
            };
        }
        catch
        {
            return new WorldSettings();
        }
    }

    /// <summary>Convert a difficulty byte (0–3) to its name.</summary>
    public static string DifficultyName(byte b) => b switch
    {
        0 => "peaceful", 1 => "easy", 2 => "normal", 3 => "hard", _ => "normal",
    };

    /// <summary>Convert a difficulty name to its byte value.</summary>
    public static byte DifficultyByte(string name)
        => DifficultyValues.TryGetValue(name.ToLowerInvariant(), out byte b) ? b : (byte)2;

    /// <summary>
    /// Persist a difficulty change, a GameType change (survival/creative/adventure/spectator), and a
    /// set of gamerule toggles back into the world's level.dat, editing the NBT in place (no full
    /// re-serialization) so every other tag — SpawnPoint, Player, Version, RandomSeed, etc. — is
    /// preserved byte-for-byte. The file is gzip-compressed NBT; we decompress, edit, recompress, and
    /// write atomically (level.dat.tmp → replace). Unknown/absent tags are skipped (we only touch the
    /// ones we can find).
    /// </summary>
    /// <returns>The <see cref="WorldSettings"/> as they now appear on disk (a fresh read-back).</returns>
    public static WorldSettings Write(string worldDir, WorldSettings settings)
    {
        string levelDat = Path.Combine(worldDir, "level.dat");
        if (!File.Exists(levelDat))
            throw new FileNotFoundException("level.dat not found — cannot edit world settings.", levelDat);

        // Decompress the gzip'd NBT into a mutable byte buffer.
        byte[] nbt;
        using (var fs = File.OpenRead(levelDat))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            gz.CopyTo(ms);
            nbt = ms.ToArray();
        }

        // --- Difficulty: a TAG_Byte whose value is exactly one byte → safe in-place overwrite. ---
        byte diffByte = DifficultyByte(settings.Difficulty);
        nbt = ReplaceByteTag(nbt, "Difficulty", diffByte);

        // --- GameType: a TAG_Int (4-byte big-endian) — fixed size, safe in-place overwrite. ---
        if (settings.GameType is not null)
        {
            nbt = ReplaceIntTag(nbt, "GameType", GameTypeInt(settings.GameType));
        }

        // --- SpawnProtection: TAG_Int radius (blocks). Clamped to [0, 32767] (negative/absurd values
        // would behave oddly). Only written when the caller supplied a non-null value. ---
        if (settings.SpawnProtectionRadius is int radius)
        {
            nbt = ReplaceIntTag(nbt, "SpawnProtection", Math.Clamp(radius, 0, short.MaxValue));
        }

        // --- allowCommands (cheats): TAG_Byte 0/1. ---
        if (settings.AllowCommands is bool ac)
        {
            nbt = ReplaceByteTag(nbt, "allowCommands", ac ? (byte)1 : (byte)0);
        }

        // --- hardcore: TAG_Byte 0/1. When on, the world is locked to hard difficulty + permadeath. ---
        if (settings.Hardcore is bool hc)
        {
            nbt = ReplaceByteTag(nbt, "hardcore", hc ? (byte)1 : (byte)0);
        }

        // --- GameRules: each rule is a TAG_String whose value may be "true"(4) or "false"(5). ---
        // Only touch the rules the caller actually supplied (so editing one rule never silently
        // clobbers the others). When the new value has a different length than the old one we
        // rebuild that string payload (length prefix + bytes) so the rest of the NBT shifts correctly.
        foreach ((string rule, string rawValue) in settings.GameRules)
        {
            // Normalize to "true"/"false" so a stray casing never leaves a non-boolean in level.dat.
            string newValue = string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
            nbt = ReplaceStringTag(nbt, rule, newValue);
        }

        // Recompress + write atomically.
        string tmp = levelDat + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        {
            gz.Write(nbt, 0, nbt.Length);
        }
        File.Copy(tmp, levelDat, overwrite: true);
        File.Delete(tmp);

        // Read back so callers (and tests) see exactly what landed on disk.
        return Read(worldDir);
    }

    /// <summary>
    /// Rename the world's in-game display name by rewriting the <c>Data.LevelName</c> string tag
    /// in level.dat. Uses the same in-place NBT edit as <see cref="Write"/>: decompress gzip, splice
    /// the new string value (rebuilding the length prefix when it differs in length), recompress,
    /// write atomically. Returns the new name on success. Throws when level.dat is missing or the
    /// <c>LevelName</c> tag can't be located (older/foreign level.dat shapes).
    /// </summary>
    public static string WriteLevelName(string worldDir, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("World name must not be empty.", nameof(newName));

        string levelDat = Path.Combine(worldDir, "level.dat");
        if (!File.Exists(levelDat))
            throw new FileNotFoundException("level.dat not found — cannot rename world.", levelDat);

        byte[] nbt;
        using (var fs = File.OpenRead(levelDat))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            gz.CopyTo(ms);
            nbt = ms.ToArray();
        }

        byte[] edited = ReplaceStringTag(nbt, "LevelName", newName);
        // ReplaceStringTag returns the buffer unchanged when the tag isn't found — detect that so
        // we don't silently succeed while leaving the old name in place.
        int valueOffset = FindStringTagOffset(nbt, "LevelName");
        if (valueOffset < 0)
            throw new InvalidDataException("level.dat has no LevelName tag — cannot rename.");

        string tmp = levelDat + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            gz.Write(edited, 0, edited.Length);
        File.Copy(tmp, levelDat, overwrite: true);
        File.Delete(tmp);
        return newName;
    }

    // --- NBT byte-scanning helpers (same minimal approach as WorldMetadataReader) ---

    /// <summary>Find a TAG_Byte value by its tag name. Returns the default when not found.</summary>
    private static byte FindByteTag(byte[] nbt, string tagName, byte defaultValue)
    {
        byte[] needle = Encoding.ASCII.GetBytes(tagName);
        for (int i = 1; i < nbt.Length - needle.Length - 3; i++)
        {
            if (nbt[i] != 0x01) continue; // TAG_Byte id
            if (nbt[i + 1] != 0x00 || nbt[i + 2] != needle.Length) continue;
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (nbt[i + 3 + j] != needle[j]) { match = false; break; }
            if (!match) continue;
            // The value follows the name: 1 byte.
            int valOff = i + 3 + needle.Length;
            if (valOff < nbt.Length) return nbt[valOff];
        }
        return defaultValue;
    }

    /// <summary>Find a TAG_String value by its tag name. Returns null when not found.</summary>
    private static string? FindStringTag(byte[] nbt, string tagName)
    {
        int valueOffset = FindStringTagOffset(nbt, tagName);
        if (valueOffset < 0) return null;
        if (valueOffset + 2 > nbt.Length) return null;
        int valLen = (nbt[valueOffset] << 8) | nbt[valueOffset + 1];
        if (valLen < 0 || valueOffset + 2 + valLen > nbt.Length) return null;
        return Encoding.UTF8.GetString(nbt, valueOffset + 2, valLen);
    }

    /// <summary>
    /// Replace the value of a named TAG_Byte in place. The byte value is always exactly 1 byte, so
    /// the NBT layout never shifts — we just overwrite the one value byte after the name. When the
    /// tag isn't found the buffer is returned unchanged (we won't invent a tag we don't understand
    /// how to nest correctly).
    /// </summary>
    private static byte[] ReplaceByteTag(byte[] nbt, string tagName, byte newValue)
    {
        byte[] needle = Encoding.ASCII.GetBytes(tagName);
        for (int i = 1; i < nbt.Length - needle.Length - 3; i++)
        {
            if (nbt[i] != 0x01) continue; // TAG_Byte id
            if (nbt[i + 1] != 0x00 || nbt[i + 2] != needle.Length) continue;
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (nbt[i + 3 + j] != needle[j]) { match = false; break; }
            if (!match) continue;
            int valOff = i + 3 + needle.Length;
            if (valOff < nbt.Length)
            {
                nbt[valOff] = newValue; // single-byte overwrite, no layout shift
                return nbt;
            }
        }
        return nbt; // tag not found → leave untouched
    }

    /// <summary>Find a TAG_Int (id 0x03) value by its tag name. Returns the default when not found.</summary>
    private static int FindIntTag(byte[] nbt, string tagName, int defaultValue)
    {
        int off = FindFixedTagValueOffset(nbt, tagName, 0x03);
        if (off < 0 || off + 4 > nbt.Length) return defaultValue;
        // 4-byte big-endian.
        return (nbt[off] << 24) | (nbt[off + 1] << 16) | (nbt[off + 2] << 8) | nbt[off + 3];
    }

    /// <summary>
    /// Replace the value of a named TAG_Int in place. An int is always exactly 4 bytes, so the NBT
    /// layout never shifts — we overwrite the four value bytes (big-endian) after the name. When the
    /// tag isn't found the buffer is returned unchanged.
    /// </summary>
    private static byte[] ReplaceIntTag(byte[] nbt, string tagName, int newValue)
    {
        int off = FindFixedTagValueOffset(nbt, tagName, 0x03);
        if (off < 0 || off + 4 > nbt.Length) return nbt;
        nbt[off] = (byte)((newValue >> 24) & 0xFF);
        nbt[off + 1] = (byte)((newValue >> 16) & 0xFF);
        nbt[off + 2] = (byte)((newValue >> 8) & 0xFF);
        nbt[off + 3] = (byte)(newValue & 0xFF);
        return nbt;
    }

    /// <summary>
    /// Locate the value offset (into the decompressed NBT byte buffer) of a fixed-size tag by name:
    /// the byte immediately after [tagId(1)][name length(2)][name bytes]. Used by both the TAG_Byte
    /// and TAG_Int read/replace paths. Returns -1 when the tag isn't present.
    /// </summary>
    private static int FindFixedTagValueOffset(byte[] nbt, string tagName, byte tagId)
    {
        byte[] needle = Encoding.ASCII.GetBytes(tagName);
        for (int i = 1; i < nbt.Length - needle.Length - 3; i++)
        {
            if (nbt[i] != tagId) continue;
            if (nbt[i + 1] != 0x00 || nbt[i + 2] != needle.Length) continue;
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (nbt[i + 3 + j] != needle[j]) { match = false; break; }
            if (!match) continue;
            return i + 3 + needle.Length;
        }
        return -1;
    }

    /// <summary>
    /// Replace the value of a named TAG_String. Because the new value may have a different length
    /// than the old (e.g. "true"(4) vs "false"(5) when toggling a gamerule), this rebuilds the
    /// payload as [2-byte BE length][UTF-8 bytes] and splices it into a freshly sized buffer so the
    /// trailing NBT shifts correctly. Returns the buffer unchanged when the tag isn't present.
    /// </summary>
    private static byte[] ReplaceStringTag(byte[] nbt, string tagName, string newValue)
    {
        int valueOffset = FindStringTagOffset(nbt, tagName);
        if (valueOffset < 0 || valueOffset + 2 > nbt.Length) return nbt;

        int oldLen = (nbt[valueOffset] << 8) | nbt[valueOffset + 1];
        if (oldLen < 0 || valueOffset + 2 + oldLen > nbt.Length) return nbt;

        byte[] newBytes = Encoding.UTF8.GetBytes(newValue);
        // Rebuild: [everything before the length prefix][new length][new bytes][everything after old value]
        var rebuilt = new byte[nbt.Length - (2 + oldLen) + (2 + newBytes.Length)];
        Buffer.BlockCopy(nbt, 0, rebuilt, 0, valueOffset); // head (incl. tag id + name)
        rebuilt[valueOffset] = (byte)((newBytes.Length >> 8) & 0xFF); // BE length hi
        rebuilt[valueOffset + 1] = (byte)(newBytes.Length & 0xFF);    // BE length lo
        Buffer.BlockCopy(newBytes, 0, rebuilt, valueOffset + 2, newBytes.Length);
        int tailStart = valueOffset + 2 + oldLen;
        Buffer.BlockCopy(nbt, tailStart, rebuilt, valueOffset + 2 + newBytes.Length, nbt.Length - tailStart);
        return rebuilt;
    }

    /// <summary>
    /// Locate the offset (into the decompressed NBT byte buffer) of a named TAG_String's value —
    /// i.e. the position of its 2-byte big-endian length prefix. Returns -1 when not found.
    /// Shared between the read and write paths so the writer edits exactly the tag the reader sees.
    /// </summary>
    private static int FindStringTagOffset(byte[] nbt, string tagName)
    {
        byte[] needle = Encoding.ASCII.GetBytes(tagName);
        for (int i = 1; i < nbt.Length - needle.Length - 5; i++)
        {
            if (nbt[i] != 0x08) continue; // TAG_String id
            if (nbt[i + 1] != 0x00 || nbt[i + 2] != needle.Length) continue;
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (nbt[i + 3 + j] != needle[j]) { match = false; break; }
            if (!match) continue;
            return i + 3 + needle.Length; // points at the length prefix of the value
        }
        return -1;
    }
}

/// <summary>The world settings read from / written to a world's level.dat.</summary>
public sealed record WorldSettings
{
    public string Difficulty { get; init; } = "normal";

    /// <summary>Game mode: survival/creative/adventure/spectator, or null when not yet read/edited.</summary>
    public string? GameType { get; init; }

    /// <summary>Spawn-protection radius in blocks (0 = disabled). null = not read/edited.</summary>
    public int? SpawnProtectionRadius { get; init; }

    /// <summary>True when cheat commands are allowed (allowCommands=1). null = not read/edited.</summary>
    public bool? AllowCommands { get; init; }

    /// <summary>True when hardcore mode is on (locked hard difficulty + permadeath). null = not read/edited.</summary>
    public bool? Hardcore { get; init; }

    public IReadOnlyDictionary<string, string> GameRules { get; init; } = new Dictionary<string, string>();

    public WorldSettings() { }

    public WorldSettings(string difficulty, IReadOnlyDictionary<string, string> gameRules)
        : this(difficulty, gameType: null, gameRules) { }

    public WorldSettings(string difficulty, string? gameType, IReadOnlyDictionary<string, string> gameRules)
    {
        Difficulty = difficulty;
        GameType = gameType;
        GameRules = gameRules ?? new Dictionary<string, string>();
    }

    /// <summary>True when a gamerule is set to "true" (case-insensitive).</summary>
    public bool IsRuleEnabled(string rule)
        => GameRules.TryGetValue(rule, out var v) && string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
}
