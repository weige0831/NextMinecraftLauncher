using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NML.App.Services;
using NML.Core;
using NML.Core.Logging;
using NML.Core.Auth;
using NML.Core.Auth.AuthlibInjector;
using NML.Core.Download;
using NML.Core.Instances;
using NML.Core.Java;
using NML.Core.Launch;
using NML.Core.Models;
using NML.Core.Modpacks;

namespace NML.App.ViewModels.Pages;

/// <summary>
/// Home/launch page: shows the user's instances, lets them pick one and launch it.
/// Reuses the full engine pipeline (VersionInfo → Java → command → process) and auto-runs
/// the crash analyzer on non-zero exit.
/// </summary>
public partial class HomePageViewModel : PageViewModelBase
{
    public override string TitleKey => "nav.home";
    public override string Icon => "⌂";

    private readonly VersionManifestService _manifest;
    private readonly VanillaInstaller _vanillaInstaller;
    private readonly VersionInfoService _versions;
    private readonly JavaRuntimeDetector _javaDetector;
    private readonly LaunchCommandBuilder _launcher;
    private readonly ProcessLauncher _processLauncher;
    private readonly InstanceStore _instances;
    private readonly IOfflineAuthProvider _offline;
    private readonly SettingsStore _settings;
    private readonly CrashAnalyzerFactory? _crashFactory;
    private readonly AuthlibInjectorSetup? _authlibInjectorSetup;
    private readonly AccountStore? _activeAccountStore;
    private readonly InstanceTransferService? _instanceTransfer;
    private readonly ModpackInstaller? _modpackInstaller;
    private readonly Core.Modloaders.FabricInstaller? _fabricInstaller;
    private readonly Core.Modloaders.QuiltInstaller? _quiltInstaller;
    private readonly Core.Modloaders.ForgeInstaller? _forgeInstaller;
    private readonly Core.Modloaders.NeoForgeInstaller? _neoForgeInstaller;
    private readonly Core.Modloaders.OptiFineInstaller? _optifineInstaller;
    private readonly Core.Modloaders.LiteLoaderInstaller? _liteloaderInstaller;
    private readonly ILogger<HomePageViewModel> _logger;

    /// <summary>Available sort modes for the instance list.</summary>
    public IReadOnlyList<string> SortModes { get; } = new[] { "Name", "Version", "Created", "Favorites" };

    [ObservableProperty] private string _sortMode = "Name";

    partial void OnSortModeChanged(string value) => ApplySort();

    /// <summary>Re-sort the Instances collection by the current SortMode.</summary>
    private void ApplySort()
    {
        var sorted = SortMode switch
        {
            "Version" => Instances.OrderBy(i => i.VersionId).ThenBy(i => i.Name).ToList(),
            "Created" => Instances.OrderByDescending(i => i.CreatedAt).ToList(),
            "Favorites" => Instances.OrderByDescending(i => i.IsFavorite).ThenBy(i => i.Name).ToList(),
            _ => Instances.OrderBy(i => i.Name).ToList(),
        };

        if (sorted.SequenceEqual(Instances)) return;
        Instances.Clear();
        foreach (var inst in sorted) Instances.Add(inst);
    }

    public ObservableCollection<Instance> Instances { get; } = new();
    public ObservableCollection<string> AvailableVersions { get; } = new();

    [ObservableProperty] private Instance? _selectedInstance;
    [ObservableProperty] private string _status;
    [ObservableProperty] private string _offlineUsername = "Player";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _installProgressPercent;

    /// <summary>Fetch/refresh the version list from the manifest.</summary>
    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        IsBusy = true;
        Status = "home.status_fetching";
        try
        {
            VersionManifest m = await _manifest.GetAsync(forceRefresh: true);
            AvailableVersions.Clear();
            foreach (VersionManifestEntry v in m.Versions)
                AvailableVersions.Add(v.Id);
            Status = $"home.loaded_versions,{m.Versions.Count}";
        }
        catch (Exception ex)
        {
            Status = $"home.fetch_failed,{ex.Message}";
            _logger.LogError(ex, "Failed to refresh versions.");
        }
        finally { IsBusy = false; }
    }

    // --- New instance wizard ---
    [ObservableProperty] private bool _showNewInstanceWizard;
    [ObservableProperty] private string _newInstanceName = string.Empty;
    [ObservableProperty] private string _newInstanceVersion = string.Empty;
    [ObservableProperty] private int _newInstanceMemory = 4096;
    [ObservableProperty] private string _newInstanceModloader = "None";
    /// <summary>Wizard toggle: whether the new instance gets its own .minecraft (default) or
    /// shares the common one. Bound to a checkbox in the new-instance wizard.</summary>
    [ObservableProperty] private bool _newInstanceIsIsolated = true;

    /// <summary>Path to a modpack zip (.mrpack / CurseForge manifest) to import as a new instance.</summary>
    [ObservableProperty] private string _importModpackPath = string.Empty;

    /// <summary>Available modloaders for the wizard dropdown.</summary>
    public IReadOnlyList<string> ModloaderChoices { get; } = new[] { "None", "Fabric", "Quilt", "Forge", "NeoForge", "OptiFine", "LiteLoader" };

    /// <summary>Live game console output (stdout+stderr) as a raw string (for export + the unfiltered record).</summary>
    [ObservableProperty] private string _consoleOutput = string.Empty;

    /// <summary>Console search box text (substring or regex). Filters <see cref="ConsoleLines"/>.</summary>
    [ObservableProperty] private string _consoleSearchText = string.Empty;
    /// <summary>When true, <see cref="ConsoleSearchText"/> is treated as a regex; otherwise substring.</summary>
    [ObservableProperty] private bool _isConsoleRegexSearch;
    /// <summary>Minimum severity to show (floor). One of: Trace/Debug/Info/Warn/Error.</summary>
    [ObservableProperty] private string _consoleMinSeverity = nameof(NML.Core.Logging.LogSeverityClassifier.Severity.Trace);

    /// <summary>Severity-floor options for the console filter dropdown.</summary>
    public IReadOnlyList<string> ConsoleSeverityOptions { get; } =
        new[] { "Trace", "Debug", "Info", "Warn", "Error" };

    /// <summary>Colored, filtered console lines bound to the live console ItemsControl.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LogLineEntry> ConsoleLines { get; } = new();

    /// <summary>Every classified line since the last reset (the source the filter rebuilds from).</summary>
    private readonly List<NML.Core.Logging.LogLine> _allConsoleLines = new();
    /// <summary>Hard cap on retained console lines (keeps memory bounded on long-running games).</summary>
    private const int MaxConsoleLines = 5000;

    /// <summary>Human-readable disk usage of the selected instance (e.g. "12.3 GB total, 8.1 GB mods").</summary>
    [ObservableProperty] private string _diskUsageDisplay = string.Empty;

    /// <summary>True when disk usage is being calculated.</summary>
    [ObservableProperty] private bool _isCalculatingDiskUsage;

    // --- Deep modpack export toggles (off by default; worlds/screenshots/settings are personal
    // and large, so the user opts in per export). All bound to checkboxes in the export panel. ---
    [ObservableProperty] private bool _exportIncludeSaves;
    [ObservableProperty] private bool _exportIncludeScreenshots;
    [ObservableProperty] private bool _exportIncludeClientSettings;
    [ObservableProperty] private bool _exportIncludeLogs;

    // Batch console updates to avoid UI freeze on high-frequency game output.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _consoleBuffer = new();
    private int _consoleFlushScheduled;

    private void OnGameOutput(string line)
    {
        _consoleBuffer.Enqueue(line);

        // Schedule a flush on the UI thread if not already pending (coalesces many lines
        // into a single PropertyChanged notification per ~100ms).
        if (Interlocked.CompareExchange(ref _consoleFlushScheduled, 1, 0) == 0)
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                Interlocked.Exchange(ref _consoleFlushScheduled, 0);
                FlushConsole();
            }, TimeSpan.FromMilliseconds(100));
        }
    }

    private void FlushConsole()
    {
        var sb = new System.Text.StringBuilder();
        var newLines = new List<NML.Core.Logging.LogLine>();
        while (_consoleBuffer.TryDequeue(out string? line))
        {
            sb.AppendLine(line);
            // Classify each line so the live console can color + filter it (mirrors the logs-tab flow).
            newLines.Add(new NML.Core.Logging.LogLine(line, NML.Core.Logging.LogSeverityClassifier.Classify(line)));
        }
        if (sb.Length == 0) return;

        // Keep the raw string (for Export) bounded the same way as before.
        string next = ConsoleOutput + sb.ToString();
        if (next.Length > 5000) next = next[^5000..];
        ConsoleOutput = next;

        // Append classified lines + cap the buffer (drop oldest beyond the cap).
        _allConsoleLines.AddRange(newLines);
        if (_allConsoleLines.Count > MaxConsoleLines)
            _allConsoleLines.RemoveRange(0, _allConsoleLines.Count - MaxConsoleLines);

        RebuildFilteredConsole();
    }

    /// <summary>
    /// Rebuild <see cref="ConsoleLines"/> from <see cref="_allConsoleLines"/> applying the current
    /// severity floor + substring/regex search. Called on each flush and when the filter inputs
    /// change. Mirrors <c>GameContentPageViewModel.RebuildFilteredLog</c>.
    /// </summary>
    private void RebuildFilteredConsole()
    {
        ConsoleLines.Clear();
        if (_allConsoleLines.Count == 0) return;

        if (!Enum.TryParse<NML.Core.Logging.LogSeverityClassifier.Severity>(ConsoleMinSeverity, out var floor))
            floor = NML.Core.Logging.LogSeverityClassifier.Severity.Trace;

        Regex? regex = null;
        bool hasSearch = !string.IsNullOrWhiteSpace(ConsoleSearchText);
        if (hasSearch && IsConsoleRegexSearch)
        {
            try { regex = new Regex(ConsoleSearchText, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { ConsoleLines.Clear(); return; } // invalid pattern → empty
        }

        foreach (var line in _allConsoleLines)
        {
            // Severity floor: Trace(0) shows everything; Error(4) shows only errors. Lower ordinal = more verbose.
            if ((int)line.Severity > (int)floor) continue;
            if (hasSearch)
            {
                bool match = regex is not null
                    ? regex.IsMatch(line.Text)
                    : line.Text.Contains(ConsoleSearchText, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
            }
            ConsoleLines.Add(new LogLineEntry(line.Text, line.Color));
        }
    }

    // Re-filter when the search/severity inputs change (reactive, no launch needed).
    partial void OnConsoleSearchTextChanged(string value) => RebuildFilteredConsole();
    partial void OnIsConsoleRegexSearchChanged(bool value) => RebuildFilteredConsole();
    partial void OnConsoleMinSeverityChanged(string value) => RebuildFilteredConsole();

    /// <summary>System total RAM in MB (drives the slider max + recommended hint).</summary>
    public long SystemRamMb
    {
        get
        {
            try
            {
                // GCMemoryInfo.TotalAvailableMemoryBytes gives total physical RAM on most platforms.
                return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            }
            catch { return 0; }
        }
    }

    /// <summary>Max memory slider value (clamp to system RAM, min 1024).</summary>
    public long SliderMax => Math.Max(1024, SystemRamMb > 0 ? SystemRamMb : 16384);

    /// <summary>Recommended memory for the selected instance (2/3 of system, clamped 1024..SliderMax).</summary>
    public long RecommendedMemory => SystemRamMb > 0
        ? Math.Clamp((long)(SystemRamMb * 0.66), 1024, SliderMax)
        : 4096;

    /// <summary>Two-way bindable max-memory for the selected instance.</summary>
    public int SelectedMaxMemory
    {
        get => SelectedInstance?.MaxMemoryMb ?? 2048;
        set
        {
            if (SelectedInstance is not null)
            {
                SelectedInstance.MaxMemoryMb = value;
                OnPropertyChanged();
                MarkOptionsDirty();
            }
        }
    }

    /// <summary>Two-way bindable custom JVM args for the selected instance.</summary>
    public string CustomJvmArgs
    {
        get => SelectedInstance?.CustomJvmArgs ?? string.Empty;
        set
        {
            if (SelectedInstance is not null)
            {
                SelectedInstance.CustomJvmArgs = value;
                OnPropertyChanged();
                MarkOptionsDirty();
            }
        }
    }

    /// <summary>Two-way bindable custom game args for the selected instance.</summary>
    public string CustomGameArgs
    {
        get => SelectedInstance?.CustomGameArgs ?? string.Empty;
        set
        {
            if (SelectedInstance is not null)
            {
                SelectedInstance.CustomGameArgs = value;
                OnPropertyChanged();
                MarkOptionsDirty();
            }
        }
    }

    /// <summary>Two-way bindable window width for the selected instance.</summary>
    public int SelectedWindowWidth
    {
        get => SelectedInstance?.WindowWidth ?? 854;
        set { if (SelectedInstance is not null) { SelectedInstance.WindowWidth = value; OnPropertyChanged(); MarkOptionsDirty(); } }
    }

    /// <summary>Two-way bindable window height for the selected instance.</summary>
    public int SelectedWindowHeight
    {
        get => SelectedInstance?.WindowHeight ?? 480;
        set { if (SelectedInstance is not null) { SelectedInstance.WindowHeight = value; OnPropertyChanged(); MarkOptionsDirty(); } }
    }

    /// <summary>True to launch the game in fullscreen mode (--fullscreen arg).</summary>
    [ObservableProperty] private bool _launchFullscreen;

    /// <summary>Export the live console output to a .txt file on the desktop (HMCL feature).</summary>
    [RelayCommand]
    private void ExportConsoleLog()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = Path.Combine(desktop, $"nml-console-{stamp}.txt");
            File.WriteAllText(path, ConsoleOutput ?? "(no output)");
            Status = $"Log exported: {path}";
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
    }

    partial void OnLaunchFullscreenChanged(bool value)
    {
        // Toggle --fullscreen in the game args.
        if (SelectedInstance is null) return;
        string args = SelectedInstance.CustomGameArgs ?? string.Empty;
        if (value && !args.Contains("--fullscreen", StringComparison.OrdinalIgnoreCase))
        {
            SelectedInstance.CustomGameArgs = string.IsNullOrWhiteSpace(args) ? "--fullscreen" : $"{args} --fullscreen";
            OnPropertyChanged(nameof(CustomGameArgs));
            MarkOptionsDirty();
        }
        else if (!value && args.Contains("--fullscreen", StringComparison.OrdinalIgnoreCase))
        {
            SelectedInstance.CustomGameArgs = args.Replace("--fullscreen", "", StringComparison.OrdinalIgnoreCase).Trim();
            OnPropertyChanged(nameof(CustomGameArgs));
            MarkOptionsDirty();
        }
    }

    /// <summary>Two-way bindable isolation mode for the selected instance (own .minecraft vs shared).
    /// Persisted via the existing Save launch options command.</summary>
    public bool SelectedInstanceIsIsolated
    {
        get => SelectedInstance?.IsIsolated ?? true;
        set { if (SelectedInstance is not null && SelectedInstance.IsIsolated != value) { SelectedInstance.IsIsolated = value; OnPropertyChanged(); MarkOptionsDirty(); } }
    }

    /// <summary>True when the selected instance's launch options have unsaved edits
    /// (drives the Save button's enabled state + a "modified" indicator).</summary>
    [ObservableProperty] private bool _isInstanceOptionsDirty;

    /// <summary>Mark the current instance's options as edited-but-not-yet-persisted.</summary>
    private void MarkOptionsDirty()
    {
        if (!IsInstanceOptionsDirty) IsInstanceOptionsDirty = true;
    }

    /// <summary>Toggle the selected instance's favorite/star state, persist, and re-sort.</summary>
    [RelayCommand]
    private void ToggleFavorite(Instance instance)
    {
        if (instance is null) return;
        instance.IsFavorite = !instance.IsFavorite;
        var all = _instances.LoadAll();
        int idx = all.FindIndex(i => string.Equals(i.Name, instance.Name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) { all[idx] = instance; _instances.SaveAll(all); }
        ApplySort();
    }

    /// <summary>
    /// Persist the selected instance's edited launch options (memory, window size, JVM/game
    /// args) back to <c>instances.json</c>. Without this the in-memory edits would be lost on
    /// restart — the original launch-options panel mutated the model but never saved it.
    /// </summary>
    [RelayCommand]
    private void SaveInstanceOptions()
    {
        if (SelectedInstance is null) return;
        try
        {
            // Re-read the persisted list so we don't clobber concurrent changes (e.g. a new
            // instance added elsewhere), then replace the matching entry and save.
            var all = _instances.LoadAll();
            int idx = all.FindIndex(i => string.Equals(i.Name, SelectedInstance.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) all[idx] = SelectedInstance;
            _instances.SaveAll(all);
            IsInstanceOptionsDirty = false;
            Status = "instance.saved";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }

    /// <summary>
    /// Migrate the selected instance's user content (saves, mods, configs, settings) to the
    /// opposite isolation mode, then flip the flag — the non-destructive counterpart to the bare
    /// IsIsolated toggle. Computes the source (current mode) + destination (opposite mode), copies
    /// via InstanceMigrator (files copied not moved, merge-prefers-newer), then switches + saves.
    /// </summary>
    [RelayCommand]
    private async Task MigrateIsolationAsync()
    {
        Instance? inst = SelectedInstance;
        if (inst is null) { Status = "home.select_first"; return; }
        try
        {
            // Capture the current (pre-flip) game dir, then compute the opposite mode's dir.
            string sourceDir = _instances.GameDirFor(inst);
            bool targetIsolated = !inst.IsIsolated;
            // Compute the destination via a flipped instance without mutating the real one
            // (Instance is a class, not a record, so build a temporary copy).
            var flipped = new Instance
            {
                Name = inst.Name,
                IsIsolated = targetIsolated,
            };
            string destDir = _instances.GameDirFor(flipped);

            var report = await Task.Run(() => InstanceMigrator.Migrate(sourceDir, destDir));
            // Flip the flag + persist so subsequent launches use the migrated dir.
            inst.IsIsolated = targetIsolated;
            SelectedInstanceIsIsolated = targetIsolated;
            SaveInstanceOptions();
            Status = $"migration.done,{report.FilesCopied}";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; _logger.LogError(ex, "Isolation migration failed."); }
    }

    /// <summary>Switching instances clears the dirty flag (the new instance shows its own
    /// persisted state, not the previous one's unsaved edits).</summary>
    partial void OnSelectedInstanceChanged(Instance? value)
    {
        IsInstanceOptionsDirty = false;
        OnPropertyChanged(nameof(SelectedInstanceIsIsolated));
        // Refresh the toggle when switching instances
        _ = RefreshDiskUsageAsync();
    }

    /// <summary>Calculate the selected instance's disk usage on a background thread.</summary>
    private async Task RefreshDiskUsageAsync()
    {
        Instance? inst = SelectedInstance;
        if (inst is null) { DiskUsageDisplay = string.Empty; return; }
        IsCalculatingDiskUsage = true;
        try
        {
            var usage = await Task.Run(() => NML.Core.Instances.InstanceDiskUsageCalculator.Measure(
                _instances.GameDirFor(inst)));
            if (usage.TotalBytes == 0)
            {
                DiskUsageDisplay = string.Empty;
            }
            else
            {
                var top = usage.Categories.Take(3);
                string breakdown = string.Join(", ", top.Select(c => $"{c.SizeDisplay} {c.Folder}"));
                DiskUsageDisplay = $"💾 {usage.TotalDisplay} ({breakdown})";
            }
        }
        catch { DiskUsageDisplay = string.Empty; }
        finally { IsCalculatingDiskUsage = false; }
    }

    public HomePageViewModel(
        VersionManifestService manifest,
        VanillaInstaller vanillaInstaller,
        VersionInfoService versions,
        JavaRuntimeDetector javaDetector,
        LaunchCommandBuilder launcher,
        ProcessLauncher processLauncher,
        InstanceStore instances,
        IOfflineAuthProvider offline,
        SettingsStore settings,
        ILogger<HomePageViewModel> logger,
        CrashAnalyzerFactory? crashFactory = null,
        AuthlibInjectorSetup? authlibInjectorSetup = null,
        AccountStore? activeAccountStore = null,
        InstanceTransferService? instanceTransfer = null,
        ModpackInstaller? modpackInstaller = null,
        Core.Modloaders.FabricInstaller? fabricInstaller = null,
        Core.Modloaders.QuiltInstaller? quiltInstaller = null,
        Core.Modloaders.ForgeInstaller? forgeInstaller = null,
        Core.Modloaders.NeoForgeInstaller? neoForgeInstaller = null,
        Core.Modloaders.OptiFineInstaller? optifineInstaller = null,
        Core.Modloaders.LiteLoaderInstaller? liteloaderInstaller = null)
    {
        _manifest = manifest;
        _vanillaInstaller = vanillaInstaller;
        _versions = versions;
        _javaDetector = javaDetector;
        _launcher = launcher;
        _processLauncher = processLauncher;
        _instances = instances;
        _offline = offline;
        _settings = settings;
        _crashFactory = crashFactory;
        _authlibInjectorSetup = authlibInjectorSetup;
        _activeAccountStore = activeAccountStore;
        _instanceTransfer = instanceTransfer;
        _modpackInstaller = modpackInstaller;
        _fabricInstaller = fabricInstaller;
        _quiltInstaller = quiltInstaller;
        _forgeInstaller = forgeInstaller;
        _neoForgeInstaller = neoForgeInstaller;
        _optifineInstaller = optifineInstaller;
        _liteloaderInstaller = liteloaderInstaller;
        _logger = logger;
        EnsureLanguageSubscribed();
        Status = "home.status_ready";

        foreach (Instance inst in _instances.LoadAll()) Instances.Add(inst);
    }

    public override Task OnNavigatedToAsync()
    {
        // Refresh the instance list in case another page added/removed one.
        var all = _instances.LoadAll();
        if (Instances.Count != all.Count)
        {
            Instances.Clear();
            foreach (Instance inst in all) Instances.Add(inst);
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (SelectedInstance is null) { Status = "home.select_first"; return; }
        Instance inst = SelectedInstance;
        var mc = new MinecraftDirectory(_instances.GameDirFor(inst));
        Directory.CreateDirectory(mc.Root);

        IsBusy = true;
        Status = "home.status_ready";
        try
        {
            VersionInfo version = await _versions.GetAsync(inst.VersionId, mc);

            List<JavaRuntime> runtimes = _javaDetector.DetectAll();
            int requiredMajor = version.JavaVersion?.MajorVersion ?? 17;
            JavaRuntime? java = inst.Java
                             ?? _javaDetector.FindForVersion(requiredMajor, runtimes)
                             ?? runtimes.FirstOrDefault();
            if (java is null) { Status = $"home.no_java,{requiredMajor}"; return; }

            // Pre-launch Java compatibility check: block a runtime that's older than the version's
            // required major (e.g. Java 8 for 1.17+, which would crash instantly). Surface a clear
            // status instead of launching into an immediate crash.
            var compat = JavaVersionValidator.Validate(requiredMajor, java);
            if (!compat.Ok)
            {
                Status = compat.Reason == JavaIncompatibilityReason.Missing
                    ? $"home.no_java,{requiredMajor}"
                    : $"java.check.incompatible,{java.MajorVersion},{requiredMajor}";
                return;
            }

            // Default to an offline account; if the active account is Microsoft or
            // authlib-injector (external Yggdrasil), use it instead.
            Account account = _offline.Create(OfflineUsername);
            AuthlibInjectorServer? authlibServer = null;
            string? authlibJarPath = null;

            // Pull the active account from the AccountStore.
            Account? activeAccount = _activeAccountStore?.LoadAll()
                .FirstOrDefault(a => a.Uuid == _activeAccountStore?.GetActiveUuid());

            if (activeAccount is not null)
            {
                // Use the real account (Microsoft or authlib-injector) instead of offline.
                account = activeAccount;

                // If it's an authlib-injector account, reconstruct the server + ensure the
                // agent jar is cached before launching.
                if (activeAccount.AccountType == "authlib-injector"
                    && _authlibInjectorSetup is not null
                    && !string.IsNullOrEmpty(activeAccount.Xuid))
                {
                    authlibServer = new AuthlibInjectorServer
                    {
                        Name = activeAccount.Username,
                        ApiUrl = activeAccount.Xuid, // server URL is stashed here on login
                    };
                    authlibJarPath = await _authlibInjectorSetup.EnsureAgentJarAsync();
                }
            }

            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "0.1.0";

            var opts = new LaunchOptions
            {
                Version = version, Mc = mc, Account = account, Java = java,
                MinMemoryMb = inst.MinMemoryMb, MaxMemoryMb = inst.MaxMemoryMb,
                WindowWidth = inst.WindowWidth, WindowHeight = inst.WindowHeight,
                LauncherName = "NextMinecraftLauncher",
                LauncherVersion = assemblyVersion,
                ExtraJvmArgs = string.IsNullOrWhiteSpace(inst.CustomJvmArgs)
                    ? Array.Empty<string>()
                    : inst.CustomJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                AuthlibInjectorServer = authlibServer,
                AuthlibInjectorJarPath = authlibJarPath,
            };
            List<string> argv = _launcher.Build(opts);

            string logFile = Path.Combine(mc.Root, "logs", $"launch-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
            Process process = _processLauncher.Launch(opts, argv, logFile);
            // Subscribe to live output for the console panel.
            _processLauncher.GameOutputReceived += OnGameOutput;
            ConsoleOutput = string.Empty;
            _allConsoleLines.Clear();
            ConsoleLines.Clear();
            Status = $"home.launched,{inst.VersionId},{process.Id}";

            // HMCL-style: auto-backup the active instance's worlds periodically while the game runs.
            // The timer lives only for this launch; cancellation is tied to process exit below.
            var backupCts = new CancellationTokenSource();
            Task? backupTask = StartAutoBackupAsync(mc, backupCts.Token);

            // HMCL-style: minimize the launcher after the game starts.
            MinimizeLauncherWindow();

            await process.WaitForExitAsync();
            // Stop the periodic auto-backup and take a final snapshot on exit (HMCL "backup on exit").
            backupCts.Cancel();
            try { if (backupTask is not null) await backupTask; } catch { /* non-fatal */ }
            backupCts.Dispose();
            FinalBackupOnExit(mc);

            _processLauncher.GameOutputReceived -= OnGameOutput;
            Status = process.ExitCode != 0 ? $"home.crashed,{process.ExitCode}" : "home.clean_exit";
            if (process.ExitCode != 0) await DiagnoseCrashAsync(logFile);

            // HMCL-style: restore the window when the game exits.
            RestoreLauncherWindow();
        }
        catch (Exception ex)
        {
            Status = $"home.launch_failed,{ex.Message}";
            _logger.LogError(ex, "Launch failed.");
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Run a periodic world-backup loop on a background thread while a game is running. Each tick
    /// backs up every save in the active instance and prunes old backups to the configured keep-count.
    /// The loop exits cleanly when <paramref name="ct"/> is cancelled (on game exit). Non-fatal on any
    /// error — a backup failure never disturbs the running game.
    /// </summary>
    private Task StartAutoBackupAsync(MinecraftDirectory mc, CancellationToken ct)
    {
        LauncherSettings s = _settings.Load();
        if (s.AutoBackupWorlds != true) return Task.CompletedTask; // opt-in
        int intervalMin = s.AutoBackupIntervalMinutes is { } im && im > 0 ? im : 30;
        int keep = s.AutoBackupKeepCount ?? 10;

        return Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMin));
                while (await timer.WaitForNextTickAsync(ct))
                {
                    BackupAllWorlds(mc, keep);
                }
            }
            catch (OperationCanceledException) { /* expected on game exit */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Auto-backup loop ended unexpectedly."); }
        }, ct);
    }

    /// <summary>One-shot backup of every world on game exit (the HMCL "backup on exit" snapshot).</summary>
    private void FinalBackupOnExit(MinecraftDirectory mc)
    {
        LauncherSettings s = _settings.Load();
        if (s.AutoBackupWorlds != true) return;
        int keep = s.AutoBackupKeepCount ?? 10;
        try { BackupAllWorlds(mc, keep); }
        catch (Exception ex) { _logger.LogWarning(ex, "Final on-exit backup failed."); }
    }

    /// <summary>Back up every save folder under <paramref name="mc"/> and prune old backups.</summary>
    private void BackupAllWorlds(MinecraftDirectory mc, int keepCount)
    {
        var browser = new GameContentBrowser(mc);
        foreach (var save in browser.ListSaves())
        {
            try { browser.BackupWorld(save.Path); }
            catch { /* a single world failing shouldn't abort the rest */ }
        }
        try { if (keepCount > 0) browser.PruneOldBackups(keepCount); }
        catch { /* pruning is best-effort */ }
    }


    private async Task DiagnoseCrashAsync(string launchLogPath)
    {
        if (_crashFactory?.TryCreate() is not { } analyzer) return;
        string? crashText = File.Exists(launchLogPath) ? File.ReadAllText(launchLogPath) : null;
        if (crashText is null) return;
        try
        {
            Status = "home.diagnosing";
            var d = await analyzer.AnalyzeAsync(crashText);
            Status = $"diagnosis|{d.Confidence}|{d.RootCause}";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Crash diagnosis failed."); }
    }
    private void MinimizeLauncherWindow()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { } window)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => window.WindowState = Avalonia.Controls.WindowState.Minimized);
            }
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Restore the launcher window when the game exits.</summary>
    private void RestoreLauncherWindow()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { } window)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    window.WindowState = Avalonia.Controls.WindowState.Normal;
                    window.Activate();
                });
            }
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Memory preset names for the quick-select dropdown (HMCL feature).</summary>
    public IReadOnlyList<string> MemoryPresets { get; } = new[] { "Auto", "Low (1GB)", "Medium (4GB)", "High (8GB)", "Custom" };

    [ObservableProperty] private string _selectedMemoryPreset = "Auto";

    partial void OnSelectedMemoryPresetChanged(string value)
    {
        if (SelectedInstance is null) return;
        int mb = value switch
        {
            "Low (1GB)" => 1024,
            "Medium (4GB)" => 4096,
            "High (8GB)" => 8192,
            "Custom" => SelectedMaxMemory, // don't change
            _ => (int)Math.Clamp((long)(SystemRamMb * 0.66), 1024, SliderMax), // Auto
        };
        if (value != "Custom")
        {
            SelectedMaxMemory = mb;
            MarkOptionsDirty();
        }
    }

    /// <summary>Open the selected instance's game directory in the file explorer (HMCL feature).</summary>
    [RelayCommand]
    private void OpenGameDir()
    {
        try
        {
            Instance? inst = SelectedInstance;
            if (inst is null) return;
            string gameDir = _instances.GameDirFor(inst);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(gameDir)
            {
                UseShellExecute = true,
            });
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// One-click "diagnose crash": locate the newest crash report + latest.log tail in the active
    /// instance's game dir, submit them to the configured AI crash analyzer, and surface the
    /// structured diagnosis (root cause + confidence) as a status. Unlike the automatic post-crash
    /// diagnosis, this is user-triggered and works even when no game was launched this session.
    /// </summary>
    [RelayCommand]
    private async Task SubmitCrashToAiAsync()
    {
        if (SelectedInstance is null) { Status = "home.select_first"; return; }
        if (_crashFactory?.TryCreate() is not { } analyzer) { Status = "crash.submit.no_ai"; return; }

        try
        {
            var inputs = LatestCrashFinder.Find(_instances.GameDirFor(SelectedInstance));
            if (!inputs.HasAny) { Status = "crash.submit.none"; return; }

            string report = inputs.CrashReportPath is not null && File.Exists(inputs.CrashReportPath)
                ? await File.ReadAllTextAsync(inputs.CrashReportPath)
                : inputs.LogTail ?? string.Empty;
            if (string.IsNullOrWhiteSpace(report)) { Status = "crash.submit.none"; return; }

            Status = "crash.submit.analyzing";
            var d = await analyzer.AnalyzeAsync(report, inputs.LogTail);
            Status = $"crash.submit.result,{d.Confidence},{d.RootCause}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual crash submission failed.");
            Status = "crash.submit.failed";
        }
    }

    /// <summary>Export the selected instance to a .zip bundle (instance.json + mods + config).</summary>
    [RelayCommand]
    private void ExportInstance(Instance instance)
    {
        if (_instanceTransfer is null || instance is null) return;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string zipPath = Path.Combine(desktop, $"{instance.Name}-export.zip");
            _instanceTransfer.Export(instance, zipPath);
            Status = $"home.exported,{zipPath}";
        }
        catch (Exception ex) { Status = $"home.launch_failed,{ex.Message}"; _logger.LogError(ex, "Export failed."); }
    }

    /// <summary>
    /// Deep export: bundle the instance with the user-selected optional contents (worlds,
    /// screenshots, client settings, logs) on top of the always-included mods/config dirs, so a
    /// fully-checked export reproduces the instance faithfully on another machine. Output is a
    /// distinct <c>-deep-export.zip</c> so it never silently overwrites a basic export.
    /// </summary>
    [RelayCommand]
    private void ExportDeepInstance(Instance instance)
    {
        if (_instanceTransfer is null || instance is null) return;
        try
        {
            var options = new ModpackExportOptions
            {
                IncludeSaves = ExportIncludeSaves,
                IncludeScreenshots = ExportIncludeScreenshots,
                IncludeClientSettings = ExportIncludeClientSettings,
                IncludeLogs = ExportIncludeLogs,
            };
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string zipPath = Path.Combine(desktop, $"{instance.Name}-deep-export.zip");
            _instanceTransfer.ExportDeep(instance, zipPath, options);
            Status = $"modpack.exported_deep,{zipPath}";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; _logger.LogError(ex, "Deep export failed."); }
    }

    /// <summary>Import an instance from a .zip bundle.</summary>
    [RelayCommand]
    private void ImportInstance(string zipPath)
    {
        if (_instanceTransfer is null || string.IsNullOrEmpty(zipPath)) return;
        try
        {
            Instance imported = _instanceTransfer.Import(zipPath);
            Instances.Add(imported); ApplySort();
            SelectedInstance = imported;
            Status = $"home.installed,{imported.Name}";
        }
        catch (Exception ex) { Status = $"home.launch_failed,{ex.Message}"; _logger.LogError(ex, "Import failed."); }
    }

    /// <summary>
    /// Open an OS file-picker to choose a modpack/instance archive (.zip / .mrpack), then populate
    /// <see cref="ImportModpackPath"/>. HMCL-style "Browse…" instead of forcing the user to paste a path.
    /// </summary>
    [RelayCommand]
    private async Task BrowseModpackAsync()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is null) return;
            var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import modpack / instance",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Modpack / instance archive")
                    { Patterns = new[] { "*.zip", "*.mrpack" } },
                },
            });
            if (files.Count > 0) ImportModpackPath = files[0].Path.LocalPath;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "File picker failed."); }
    }

    /// <summary>
    /// Import a modpack archive (Modrinth <c>.mrpack</c>, CurseForge <c>manifest.json</c>, or an
    /// NML instance bundle) as a new instance. The format is detected from the archive contents
    /// and routed to the right handler, so the same button accepts packs from multiple sources.
    /// </summary>
    [RelayCommand]
    private async Task ImportModpackAsync()
    {
        string zipPath = ImportModpackPath?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(zipPath)) { Status = "modpack.import.needed"; return; }
        if (!File.Exists(zipPath)) { Status = "modpack.import.not_found"; return; }

        // Detect the format up front so the status tells the user what we recognized.
        ModpackFormat fmt = ModpackFormatDetector.DetectFile(zipPath);
        Status = fmt switch
        {
            ModpackFormat.Modrinth       => "modpack.import.detected_modrinth",
            ModpackFormat.CurseForge     => "modpack.import.detected_curseforge",
            ModpackFormat.InstanceBundle => "modpack.import.detected_instance",
            _                            => "modpack.import.detected_unknown",
        };

        try
        {
            string instanceName = Path.GetFileNameWithoutExtension(zipPath);
            // Instance bundles re-use the dedicated instance-transfer import path (preserves the
            // bundled mods/config/launch-options), keeping behavior consistent with .nml-import.
            if (fmt == ModpackFormat.InstanceBundle && _instanceTransfer is not null)
            {
                Instance imported = _instanceTransfer.Import(zipPath);
                Instances.Add(imported); ApplySort();
                SelectedInstance = imported;
                Status = $"home.installed,{imported.Name}";
                ImportModpackPath = string.Empty;
                return;
            }

            if (_modpackInstaller is null) { Status = "common.error"; return; }
            if (fmt == ModpackFormat.Unknown)
            {
                Status = "modpack.import.unrecognized";
                return;
            }

            // Install the modpack into a new isolated game dir, then register the instance.
            Instance inst = new() { Name = instanceName, VersionId = "(modpack)", IsIsolated = true };
            var mc = new MinecraftDirectory(_instances.GameDirFor(inst));
            Directory.CreateDirectory(mc.Root);
            await _modpackInstaller.InstallAsync(zipPath, instanceName, mc);
            _instances.Add(inst);
            Instances.Add(inst); ApplySort();
            SelectedInstance = inst;
            Status = $"home.installed,{instanceName}";
            ImportModpackPath = string.Empty;
        }
        catch (Exception ex)
        {
            Status = $"home.launch_failed,{ex.Message}";
            _logger.LogError(ex, "Modpack import failed.");
        }
    }

    /// <summary>Export ALL instances to .zip bundles on the Desktop.</summary>
    [RelayCommand]
    private void ExportAllInstances()
    {
        if (_instanceTransfer is null || Instances.Count == 0) return;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string exportDir = Path.Combine(desktop, "NML-Instances-Export");
            Directory.CreateDirectory(exportDir);
            int count = 0;
            foreach (Instance inst in Instances)
            {
                string zipPath = Path.Combine(exportDir, $"{inst.Name}-export.zip");
                _instanceTransfer.Export(inst, zipPath);
                count++;
            }
            Status = $"home.exported,{exportDir} ({count})";
        }
        catch (Exception ex) { Status = $"home.launch_failed,{ex.Message}"; _logger.LogError(ex, "Batch export failed."); }
    }

    /// <summary>Remove a single instance from the list + store.</summary>
    [RelayCommand]
    private void RemoveInstance(Instance instance)
    {
        if (instance is null) return;
        Instances.Remove(instance);
        _instances.Remove(instance.Name);
        if (SelectedInstance?.Name == instance.Name)
            SelectedInstance = Instances.FirstOrDefault();
        Status = $"accounts.remove,{instance.Name}";
    }

    /// <summary>Open the new-instance wizard dialog.</summary>
    [RelayCommand]
    private void OpenNewInstanceWizard()
    {
        NewInstanceName = $"Minecraft {DateTimeOffset.UtcNow:yyyyMMdd}";
        NewInstanceVersion = string.Empty;
        NewInstanceMemory = 4096;
        NewInstanceIsIsolated = true;
        ShowNewInstanceWizard = true;
    }

    /// <summary>Create a new instance from the wizard form, then install + launch it.</summary>
    [RelayCommand]
    private async Task CreateNewInstanceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewInstanceName) || string.IsNullOrWhiteSpace(NewInstanceVersion))
        {
            Status = "home.select_first";
            return;
        }

        ShowNewInstanceWizard = false;
        string name = NewInstanceName.Trim();
        string versionId = NewInstanceVersion.Trim();

        // Deduplicate name.
        var existing = _instances.LoadAll();
        int suffix = 1;
        while (existing.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{NewInstanceName.Trim()} ({suffix++})";

        var instance = new Instance
        {
            Name = name,
            VersionId = versionId,
            MaxMemoryMb = NewInstanceMemory,
            MinMemoryMb = Math.Min(1024, NewInstanceMemory / 2),
            IsIsolated = NewInstanceIsIsolated,
        };

        var mc = new MinecraftDirectory(_instances.GameDirFor(instance));
        Directory.CreateDirectory(mc.Root);

        IsBusy = true;
        Status = $"home.installing,{versionId}";
        try
        {
            await _vanillaInstaller.InstallAsync(versionId, mc, downloadSettings: _settings.ResolveDownloadSettings(_manifest));

            // Install modloader if selected.
            string? modloaderProfileId = null;

            // Pre-install compatibility check: warn when the selected modloader type might not
            // be compatible with this game version (e.g. Forge for 1.21 which only has NeoForge).
            if (NewInstanceModloader != "None")
            {
                var compat = NML.Core.Modloaders.ModloaderCompatibilityChecker.Check(
                    NewInstanceModloader.ToLowerInvariant(), versionId, versionId);
                if (!compat.Ok)
                {
                    Status = $"home.launch_failed,{compat.Message}";
                    return;
                }
            }

            try
            {
                modloaderProfileId = NewInstanceModloader switch
                {
                    "Fabric" => _fabricInstaller is not null
                        ? await InstallFabricAsync(versionId, mc)
                        : null,
                    "Quilt" => await InstallQuiltAsync(versionId, mc),
                    "Forge" => await InstallForgeAsync(versionId, mc),
                    "NeoForge" => await InstallNeoForgeAsync(versionId, mc),
                    "OptiFine" => await InstallOptiFineAsync(versionId, mc),
                    "LiteLoader" => await InstallLiteLoaderAsync(versionId, mc),
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Modloader install failed for {Loader}; vanilla is still playable.", NewInstanceModloader);
                Status = $"home.install_failed,{NewInstanceModloader}: {ex.Message}";
            }

            instance.Modloader = NewInstanceModloader != "None" ? NewInstanceModloader : null;
            // If a modloader profile was installed, switch the instance to use it.
            if (!string.IsNullOrEmpty(modloaderProfileId))
                instance.VersionId = modloaderProfileId;

            _instances.Add(instance);
            Instances.Add(instance); ApplySort();
            SelectedInstance = instance;
            Status = $"home.installed,{instance.VersionId}";
        }
        catch (Exception ex)
        {
            Status = $"home.install_failed,{ex.Message}";
            _logger.LogError(ex, "New instance creation failed.");
        }
        finally { IsBusy = false; }
    }

    /// <summary>Cancel the new-instance wizard.</summary>
    [RelayCommand]
    private void CancelNewInstanceWizard() => ShowNewInstanceWizard = false;

    private async Task<string?> InstallFabricAsync(string versionId, MinecraftDirectory mc)
    {
        if (_fabricInstaller is null) return null;
        // Fetch latest stable loader.
        var loaders = await _fabricInstaller.ListLoadersAsync(versionId);
        var stable = loaders.FirstOrDefault(l => l.IsStable) ?? loaders.FirstOrDefault();
        if (stable is null) return null;
        return await _fabricInstaller.InstallAsync(versionId, stable.LoaderVersion, mc);
    }

    private async Task<string?> InstallQuiltAsync(string versionId, MinecraftDirectory mc)
    {
        if (_quiltInstaller is null) return null;
        var loaders = await _quiltInstaller.ListLoadersAsync(versionId);
        var stable = loaders.FirstOrDefault(l => l.IsStable) ?? loaders.FirstOrDefault();
        if (stable is null) return null;
        return await _quiltInstaller.InstallAsync(versionId, stable.LoaderVersion, mc);
    }

    private async Task<string?> InstallForgeAsync(string versionId, MinecraftDirectory mc)
    {
        if (_forgeInstaller is null) return null;
        var versions = await _forgeInstaller.ListVersionsAsync(versionId);
        var latest = versions.FirstOrDefault();
        if (latest is null) return null;
        return await _forgeInstaller.InstallAsync(versionId, latest.LoaderVersion, mc);
    }

    private async Task<string?> InstallNeoForgeAsync(string versionId, MinecraftDirectory mc)
    {
        if (_neoForgeInstaller is null) return null;
        var versions = await _neoForgeInstaller.ListVersionsAsync(versionId);
        var latest = versions.FirstOrDefault();
        if (latest is null) return null;
        return await _neoForgeInstaller.InstallAsync(versionId, latest.LoaderVersion, mc);
    }

    /// <summary>
    /// Install the latest OptiFine for <paramref name="versionId"/>. OptiFine's installer needs a Java
    /// runtime to run (it patches the vanilla jar), so we resolve one via the detector. Returns the
    /// OptiFine profile id ("OptiFine_{mc}_{type}") or null when no version/runtime is available.
    /// </summary>
    private async Task<string?> InstallOptiFineAsync(string versionId, MinecraftDirectory mc)
    {
        if (_optifineInstaller is null) return null;
        var versions = await _optifineInstaller.ListVersionsAsync(versionId);
        var latest = versions.FirstOrDefault();
        if (latest is null) return null;

        // Resolve a Java runtime (OptiFine's installer is a Java app).
        var runtimes = _javaDetector.DetectAll();
        var java = runtimes.FirstOrDefault()
                   ?? _javaDetector.FindForVersion(17, runtimes);
        if (java is null) return null;

        string installerCacheDir = System.IO.Path.Combine(mc.Root, "cache", "optifine");
        return await _optifineInstaller.InstallAsync(versionId, latest.Type, latest.Patch,
            installerCacheDir, java.ExecutablePath, mc);
    }

    /// <summary>Install the latest LiteLoader for <paramref name="versionId"/> (legacy loader, ≤ 1.12.2).</summary>
    private async Task<string?> InstallLiteLoaderAsync(string versionId, MinecraftDirectory mc)
    {
        if (_liteloaderInstaller is null) return null;
        var versions = await _liteloaderInstaller.ListVersionsAsync(versionId);
        var latest = versions.FirstOrDefault();
        if (latest is null) return null;
        return await _liteloaderInstaller.InstallAsync(latest, mc);
    }

    /// <summary>Delete ALL instances (with no confirmation in the MVP — use carefully).</summary>
    [RelayCommand]
    private void DeleteAllInstances()
    {
        try
        {
            foreach (Instance inst in Instances.ToList())
            {
                _instances.Remove(inst.Name);
            }
            Instances.Clear();
            SelectedInstance = null;
            Status = "home.deleted_all";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }

    /// <summary>Clone the selected instance (copy config + game dir to a new name).</summary>
    [RelayCommand]
    private void CloneInstance(Instance instance)
    {
        if (instance is null) return;
        try
        {
            Instance clone = _instances.Clone(instance, $"{instance.Name} (copy)");
            Instances.Add(clone); ApplySort();
            SelectedInstance = clone;
            Status = $"home.installed,{clone.Name}";
        }
        catch (Exception ex) { Status = $"home.launch_failed,{ex.Message}"; _logger.LogError(ex, "Clone failed."); }
    }

    /// <summary>Rename the selected instance (store + on-disk game dir for isolated instances).</summary>
    [RelayCommand]
    private async Task RenameInstanceAsync()
    {
        if (SelectedInstance is null) return;
        string? newName = await PromptForTextAsync(
            title: "Rename instance",
            message: $"Rename '{SelectedInstance.Name}' to:",
            defaultValue: SelectedInstance.Name);
        if (string.IsNullOrWhiteSpace(newName) ||
            string.Equals(newName, SelectedInstance.Name, StringComparison.Ordinal)) return;
        try
        {
            string oldName = SelectedInstance.Name;
            Instance renamed = _instances.Rename(oldName, newName.Trim());
            // Replace the in-memory entry and re-select it.
            var stale = Instances.FirstOrDefault(i => string.Equals(i.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (stale is not null) { Instances.Remove(stale); Instances.Add(renamed); ApplySort(); }
            SelectedInstance = renamed;
            Status = $"home.installed,{renamed.Name}";
        }
        catch (Exception ex) { Status = $"home.launch_failed,{ex.Message}"; _logger.LogError(ex, "Rename failed."); }
    }

    /// <summary>Small modal text-input dialog (Avalonia has no built-in prompt).</summary>
    private Task<string?> PromptForTextAsync(string title, string message, string defaultValue)
    {
        var tcs = new TaskCompletionSource<string?>();
        var tb = new Avalonia.Controls.TextBox
        {
            Text = defaultValue,
            Watermark = message,
            MinWidth = 320,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };
        var ok = new Avalonia.Controls.Button { Content = "OK", Width = 80 };
        var cancel = new Avalonia.Controls.Button { Content = "Cancel", Width = 80 };
        var buttons = new Avalonia.Controls.StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { ok, cancel },
        };
        var panel = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new Avalonia.Controls.TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 15 },
                tb,
                buttons,
            },
        };
        var dlg = new Avalonia.Controls.Window
        {
            Title = title,
            SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            Content = panel,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => { tcs.TrySetResult(tb.Text); dlg.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(null); // window X also cancels
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            dlg.ShowDialog(desktop.MainWindow!);
        }
        else
        {
            tcs.TrySetResult(null);
        }
        return tcs.Task;
    }

    /// <summary>Verify the selected instance's game files and re-download missing/corrupt ones.</summary>
    [RelayCommand]
    private async Task VerifyInstanceAsync()
    {
        if (SelectedInstance is null) return;
        Instance inst = SelectedInstance;
        IsBusy = true;
        Status = $"home.verifying,{inst.Name}";
        try
        {
            var mc = new MinecraftDirectory(_instances.GameDirFor(inst));
            var result = await _vanillaInstaller.VerifyInstanceAsync(inst.VersionId, mc,
                downloadSettings: _settings.ResolveDownloadSettings(_manifest));
            Status = result.Repaired == 0
                ? $"home.verify_ok,{result.Checked}"
                : $"home.verify_repaired,{result.Repaired},{result.Checked}";
        }
        catch (Exception ex) { Status = $"home.launch_failed,{ex.Message}"; _logger.LogError(ex, "Verify failed."); }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Export a runnable .bat/.sh launch script for the selected instance (HMCL's 导出启动脚本).
    /// Rebuilds the same java command the launcher would run and writes it to the desktop.
    /// </summary>
    [RelayCommand]
    private async Task ExportLaunchScriptAsync()
    {
        if (SelectedInstance is null) return;
        Instance inst = SelectedInstance;
        try
        {
            var mc = new MinecraftDirectory(_instances.GameDirFor(inst));
            VersionInfo version = await _versions.GetAsync(inst.VersionId, mc);

            List<JavaRuntime> runtimes = _javaDetector.DetectAll();
            int requiredMajor = version.JavaVersion?.MajorVersion ?? 17;
            JavaRuntime? java = inst.Java
                             ?? _javaDetector.FindForVersion(requiredMajor, runtimes)
                             ?? runtimes.FirstOrDefault();
            if (java is null) { Status = $"home.no_java,{requiredMajor}"; return; }

            // Resolve the account exactly like LaunchAsync: offline default, overridden by the
            // active stored account (Microsoft/authlib-injector) when one is set.
            Account scriptAccount = _offline.Create(OfflineUsername);
            Account? activeAcc = _activeAccountStore?.LoadAll()
                .FirstOrDefault(a => a.Uuid == _activeAccountStore?.GetActiveUuid());
            if (activeAcc is not null) scriptAccount = activeAcc;

            var opts = new LaunchOptions
            {
                Version = version, Mc = mc, Account = scriptAccount, Java = java,
                MinMemoryMb = inst.MinMemoryMb, MaxMemoryMb = inst.MaxMemoryMb,
                WindowWidth = inst.WindowWidth, WindowHeight = inst.WindowHeight,
                ExtraJvmArgs = string.IsNullOrWhiteSpace(inst.CustomJvmArgs)
                    ? Array.Empty<string>()
                    : inst.CustomJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            };
            List<string> argv = _launcher.Build(opts);

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            bool isWindows = OperatingSystem.IsWindows();
            string ext = isWindows ? "bat" : "sh";
            string safe = string.Concat(inst.Name.Where(char.IsLetterOrDigit).Take(30));
            string path = Path.Combine(desktop, $"launch-{safe}.{ext}");
            NML.Core.Launch.LaunchScriptExporter.Export(java.ExecutablePath, argv, mc.Root, path);
            Status = $"home.script_exported,{path}";
        }
        catch (Exception ex) { Status = $"home.launch_failed,{ex.Message}"; _logger.LogError(ex, "Script export failed."); }
    }


    /// <summary>Apply JVM auto-tuning recommendations to the selected instance.</summary>
    [RelayCommand]
    private void ApplyJvmTuning()
    {
        if (SelectedInstance is null) return;
        try
        {
            var rec = JvmTuningService.Recommend();
            SelectedInstance.MaxMemoryMb = rec.RecommendedMemoryMb;
            SelectedInstance.CustomJvmArgs = rec.FullArgs;
            // Trigger re-render of bound properties.
            OnPropertyChanged(nameof(SelectedMaxMemory));
            OnPropertyChanged(nameof(CustomJvmArgs));
            Status = $"home.tuning_applied,{rec.RecommendedMemoryMb}";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }

    /// <summary>Generate a share code for the selected instance.</summary>
    [ObservableProperty] private string _shareCode = string.Empty;

    [RelayCommand]
    private void ShareInstance(Instance instance)
    {
        if (instance is null) return;
        try
        {
            ShareCode = InstanceShareService.Encode(instance);
            Status = "home.share_generated";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }

    /// <summary>Import an instance from a share code.</summary>
    [RelayCommand]
    private void ImportFromShareCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        try
        {
            Instance? inst = InstanceShareService.Decode(code);
            if (inst is null) { Status = "home.share_invalid"; return; }

            var existing = _instances.LoadAll();
            string name = inst.Name;
            int suffix = 1;
            while (existing.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                name = $"{inst.Name} ({suffix++})";
            inst.Name = name;

            _instances.Add(inst);
            Instances.Add(inst);
            SelectedInstance = inst;
            ShareCode = string.Empty;
            Status = $"home.installed,{inst.Name}";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }
}
