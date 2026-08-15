using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NML.Core.Auth;
using NML.Core.Java;
using NML.Core.Models;
using NML.Core.Rules;

namespace NML.Core.Launch;

/// <summary>
/// Launch configuration: everything needed to build a Minecraft java command line and
/// spawn the game process. Built by the launcher UI/instance layer and consumed here.
/// </summary>
public sealed class LaunchOptions
{
    public required VersionInfo Version { get; init; }
    public required MinecraftDirectory Mc { get; init; }
    public required Account Account { get; init; }
    public required JavaRuntime Java { get; init; }

    /// <summary>Launcher identity reported to the game (e.g. "NextMinecraftLauncher").</summary>
    public string LauncherName { get; init; } = "NextMinecraftLauncher";
    public string LauncherVersion { get; init; } = "0.1.0";

    /// <summary>Minimum/maximum heap in megabytes. Defaults to 512 / 2048.</summary>
    public int MinMemoryMb { get; init; } = 512;
    public int MaxMemoryMb { get; init; } = 2048;

    /// <summary>Window width/height passed when <c>has_custom_resolution</c> is requested.</summary>
    public int WindowWidth { get; init; } = 854;
    public int WindowHeight { get; init; } = 480;

    /// <summary>Additional JVM args appended after the built-in ones (e.g. -Dfml.ignoreInvalidMinecraftCertificates).</summary>
    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = Array.Empty<string>();

    /// <summary>Launch-time feature flags (demo, custom resolution, quick-play).</summary>
    public IReadOnlyDictionary<string, bool> Features { get; init; }
        = new Dictionary<string, bool>();

    /// <summary>
    /// Optional external Yggdrasil (authlib-injector) server. When set, the launch command
    /// prepends <c>-javaagent:authlib-injector.jar=&lt;server URL&gt;</c> so the game's authlib
    /// talks to the community server (LittleSkin etc.) instead of Mojang. This is HMCL's
    /// signature "外置登录" feature. The account must have <c>AccountType == "authlib-injector"</c>.
    /// </summary>
    public Auth.AuthlibInjector.AuthlibInjectorServer? AuthlibInjectorServer { get; init; }

    /// <summary>
    /// Absolute path to a locally-cached <c>authlib-injector.jar</c>. Required when
    /// <see cref="AuthlibInjectorServer"/> is set. The UI resolves this via
    /// <see cref="AuthlibInjectorSetup.EnsureAgentJarAsync"/> before launching.
    /// </summary>
    public string? AuthlibInjectorJarPath { get; init; }
}

/// <summary>
/// Builds the final java command line (executable + argv) from a <see cref="LaunchOptions"/>,
/// resolving Mojang's <c>${...}</c> placeholders, evaluating argument rules for the current
/// platform, and assembling the classpath from resolved libraries.
/// </summary>
public sealed class LaunchCommandBuilder
{
    private readonly RuleContext _ruleCtx;

    public LaunchCommandBuilder(RuleContext? ruleCtx = null) => _ruleCtx = ruleCtx ?? RuleContext.Current();

    /// <summary>Assemble the complete argument list (excluding the java executable itself).</summary>
    public List<string> Build(LaunchOptions options)
    {
        var ctx = new RuleContext
        {
            OsName = _ruleCtx.OsName,
            Arch = _ruleCtx.Arch,
            OsVersion = _ruleCtx.OsVersion,
            Features = MergeFeatures(options.Features),
        };

        var replacements = BuildReplacements(options);
        string classpath = BuildClasspath(options);

        var argv = new List<string>();

        // 0) authlib-injector agent (MUST precede all other JVM args so it patches authlib
        //    before Minecraft loads it). Only when an external Yggdrasil server is configured.
        if (options.AuthlibInjectorServer is not null && !string.IsNullOrEmpty(options.AuthlibInjectorJarPath))
        {
            if (!File.Exists(options.AuthlibInjectorJarPath))
                throw new FileNotFoundException(
                    "authlib-injector.jar not found at the configured path. Ensure it is downloaded before launching.",
                    options.AuthlibInjectorJarPath);

            argv.Add(AuthlibInjectorSetup.BuildAgentArgument(
                options.AuthlibInjectorJarPath, options.AuthlibInjectorServer));
        }

        // 1) JVM arguments: memory, platform-specific ones from version.json, then classpath.
        argv.Add($"-Xms{options.MinMemoryMb}M");
        argv.Add($"-Xmx{options.MaxMemoryMb}M");
        argv.AddRange(options.ExtraJvmArgs);

        if (options.Version.Arguments is not null)
        {
            foreach (string arg in ResolveArgumentList(options.Version.Arguments.Jvm, ctx, replacements))
                argv.Add(arg);
        }

        argv.Add("-cp");
        argv.Add(classpath);

        // 2) Main class.
        argv.Add(options.Version.MainClass ?? "net.minecraft.client.main.Main");

        // 3) Game arguments — either the modern `arguments.game` array or legacy minecraftArguments.
        if (options.Version.Arguments is not null && options.Version.Arguments.Game.Count > 0)
        {
            foreach (string arg in ResolveArgumentList(options.Version.Arguments.Game, ctx, replacements))
                argv.Add(arg);
        }
        else if (!string.IsNullOrEmpty(options.Version.MinecraftArguments))
        {
            // Legacy: space-separated, every token is a literal or ${...} placeholder.
            foreach (string token in options.Version.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                argv.Add(ReplacePlaceholders(token, replacements));
        }

        return argv;
    }

    /// <summary>The platform-specific launch variables Mojang references via <c>${...}</c>.</summary>
    private static Dictionary<string, string> BuildReplacements(LaunchOptions options)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = options.Account.Username,
            ["${version_name}"] = options.Version.Id,
            ["${game_directory}"] = options.Mc.Root,
            ["${assets_root}"] = options.Mc.AssetsDir,
            ["${assets_index_name}"] = options.Version.AssetIndex?.Id ?? options.Version.Assets ?? "legacy",
            ["${auth_uuid}"] = options.Account.Uuid,
            ["${auth_access_token}"] = options.Account.AccessToken,
            ["${auth_session}"] = options.Account.AccessToken, // legacy
            ["${clientid}"] = string.Empty,
            ["${auth_xuid}"] = options.Account.Xuid,
            ["${version_type}"] = options.Version.Type,
            ["${user_type}"] = options.Account.IsOffline ? "legacy" : "msa",
            ["${natives_directory}"] = options.Mc.NativesDir,
            ["${launcher_name}"] = options.LauncherName,
            ["${launcher_version}"] = options.LauncherVersion,
            ["${classpath}"] = string.Empty, // set by caller in Build()
            ["${resolution_width}"] = options.WindowWidth.ToString(),
            ["${resolution_height}"] = options.WindowHeight.ToString(),
        };
        return dict;
    }

    private static IReadOnlyDictionary<string, bool> MergeFeatures(IReadOnlyDictionary<string, bool> launchFeatures)
    {
        var dict = new Dictionary<string, bool>(launchFeatures)
        {
            ["has_custom_resolution"] = true,
        };
        return dict;
    }

    /// <summary>
    /// Evaluate each <see cref="ArgumentElement"/> against <paramref name="ctx"/> and emit the
    /// resolved (placeholder-substituted) string literals, in order.
    /// </summary>
    private IEnumerable<string> ResolveArgumentList(
        List<ArgumentElement> elements, RuleContext ctx, Dictionary<string, string> replacements)
    {
        foreach (ArgumentElement element in elements)
        {
            if (element.IsConditional)
            {
                // An element can carry multiple rules; include if any allow AND none disallow,
                // using RuleEvaluator's "last matching wins" semantics per element.
                if (!RuleEvaluator.IsAllowed(element.Rules, ctx)) continue;
                foreach (string v in element.Values!)
                    yield return ReplacePlaceholders(v, replacements);
            }
            else if (element.Literal is not null)
            {
                yield return ReplacePlaceholders(element.Literal, replacements);
            }
        }
    }

    /// <summary>Build the platform classpath: client.jar + every OS-matching library jar.</summary>
    private string BuildClasspath(LaunchOptions options)
    {
        var parts = new List<string>();

        // The client jar for this version.
        string clientJar = options.Mc.VersionJar(options.Version.Id);
        if (File.Exists(clientJar)) parts.Add(clientJar);

        foreach (Library lib in options.Version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules, _ruleCtx)) continue;

            Downloadable? artifact = lib.Downloads?.Artifact;
            if (artifact is not null)
            {
                string rel = artifact.Path ?? lib.Coordinate.RelativePath;
                parts.Add(options.Mc.LibraryPath(rel));
            }
            else if (lib.Natives is null && !string.IsNullOrEmpty(lib.Name))
            {
                parts.Add(options.Mc.LibraryPath(lib.Coordinate.RelativePath));
            }
        }

        return string.Join(Path.PathSeparator, parts);
    }

    private static string ReplacePlaceholders(string s, Dictionary<string, string> replacements)
    {
        foreach ((string key, string value) in replacements)
            s = s.Replace(key, value);
        return s;
    }
}

/// <summary>
/// Spawns the Minecraft java process from a built command line. Captures stdout/stderr
/// for crash reporting (the AI crash analyzer consumes these) and exposes exit info.
/// </summary>
public sealed class ProcessLauncher(ILogger<ProcessLauncher> logger)
{
    private readonly ILogger<ProcessLauncher> _logger = logger;

    /// <summary>Fires for each line of game output (stdout+stderr) — consumed by the console UI.</summary>
    public event Action<string>? GameOutputReceived;

    /// <summary>
    /// Launch the game. Returns the started <see cref="Process"/>; stdout/stderr are
    /// captured to <paramref name="logSink"/> (a per-launch log file) for crash analysis.
    /// </summary>
    public Process Launch(LaunchOptions options, List<string> argv, string logFilePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.Java.ExecutablePath,
            WorkingDirectory = options.Mc.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (string a in argv) psi.ArgumentList.Add(a);

        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
        var log = new StreamWriter(logFilePath, append: false) { AutoFlush = true };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                log.WriteLine(e.Data);
                _logger.LogDebug("[MC] {Line}", e.Data);
                GameOutputReceived?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                log.WriteLine(e.Data);
                _logger.LogWarning("[MC] {Line}", e.Data);
                GameOutputReceived?.Invoke(e.Data);
            }
        };

        _logger.LogInformation("Launching {Exe} (version {Id})…", options.Java.ExecutablePath, options.Version.Id);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Close the log file when the process exits.
        process.Exited += (_, _) =>
        {
            try { log.Dispose(); } catch { /* swallow */ }
            _logger.LogInformation("Minecraft exited with code {Code}.", process.ExitCode);
        };

        return process;
    }
}
