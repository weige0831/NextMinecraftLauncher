using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NML.AICore;
using NML.AICore.LocalModels;
using NML.App.Localization;
using NML.App.Services;
using NML.Core.Download;
using NML.Core.Java;
using NML.Core.Theming;
using NML.Core.Update;

namespace NML.App.ViewModels.Pages;

/// <summary>
/// Settings page: language switcher (live-applies), Minecraft path, detected Java runtimes,
/// and AI-provider management (detect local models, add cloud providers, activate one).
/// </summary>
public partial class SettingsPageViewModel : PageViewModelBase
{
    public override string TitleKey => "nav.settings";
    public override string Icon => "⚙";

    private readonly SettingsStore _settings;
    private readonly LocalModelProbe _probe;
    private readonly ChatClientFactory _factory;
    private readonly JavaRuntimeDetector _javaDetector;
    private readonly NML.Core.Java.JavaRuntimeInstaller? _javaInstaller;
    private readonly ILogger<SettingsPageViewModel> _logger;

    /// <summary>Human-readable system info summary (CPU, RAM, OS, arch).</summary>
    public string SystemInfoDisplay { get; } = NML.Core.Platform.SystemInfoCollector.FormatSummary(
        NML.Core.Platform.SystemInfoCollector.Collect());

    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = new();

    /// <summary>True while a Java runtime is downloading/installing.</summary>
    [ObservableProperty] private bool _isInstallingJava;
    [ObservableProperty] private string _javaInstallStatus = string.Empty;

    /// <summary>Download and install a Mojang Java runtime (java-runtime-gamma = Java 17+).</summary>
    [RelayCommand]
    private async Task InstallJavaRuntimeAsync()
    {
        if (_javaInstaller is null || IsInstallingJava) return;
        IsInstallingJava = true;
        JavaInstallStatus = "Downloading Java runtime…";
        try
        {
            string runtimesRoot = System.IO.Path.Combine(_settings.SettingsDir, "runtimes");
            var jrt = await _javaInstaller.InstallAsync("java-runtime-gamma", runtimesRoot, ct: default);
            JavaInstallStatus = $"Installed Java {jrt.MajorVersion} to: {jrt.BinDirectory}";
            // Re-detect so the new runtime appears in the list.
            JavaRuntimes.Clear();
            foreach (JavaRuntime j in _javaDetector.DetectAll()) JavaRuntimes.Add(j);
        }
        catch (Exception ex)
        {
            JavaInstallStatus = $"Install failed: {ex.Message}";
            _logger.LogWarning(ex, "Java runtime install failed.");
        }
        finally { IsInstallingJava = false; }
    }
    public ObservableCollection<ChatProviderConfig> Providers { get; } = new();
    public ObservableCollection<CultureInfo> AvailableLanguages { get; } = new();

    [ObservableProperty] private CultureInfo? _selectedLanguage;
    [ObservableProperty] private string _minecraftPath = string.Empty;

    /// <summary>Max simultaneous downloads (1–64). Persisted; feeds VanillaInstaller.</summary>
    [ObservableProperty] private int _downloadConcurrency = 8;

    /// <summary>Mirror base URL (BMCLAPI-style) or empty = official Mojang endpoints. Persisted.</summary>
    [ObservableProperty] private string _downloadMirrorUrl = string.Empty;

    /// <summary>Well-known mirrors offered in the dropdown (empty entry = official).</summary>
    public IReadOnlyList<string> MirrorPresets { get; } = new[]
    {
        "", // official Mojang
        "https://bmclapi2.bangbang93.com",
        "https://mcdownload.azureedge.net",
    };

    /// <summary>User-typed custom CSS body (multi-line). Bound to the textarea; persisted + applied on Apply.</summary>
    [ObservableProperty] private string _customCss = string.Empty;

    /// <summary>UI font scale: 0.9=small, 1.0=normal, 1.1=large, 1.2=extra large.</summary>
    [ObservableProperty] private double _fontScale = 1.0;

    /// <summary>HTTP proxy URL for all launcher downloads (empty = direct).</summary>
    [ObservableProperty] private string _proxyUrl = string.Empty;
    partial void OnProxyUrlChanged(string value) => PersistSettings();

    /// <summary>Whether to check GitHub Releases for a new launcher version on startup.</summary>
    [ObservableProperty] private bool _checkForUpdatesOnStartup = true;

    partial void OnCheckForUpdatesOnStartupChanged(bool value) => PersistSettings();

    /// <summary>Whether to auto-backup the active instance's worlds periodically while a game runs + on exit.</summary>
    [ObservableProperty] private bool _autoBackupWorlds;
    partial void OnAutoBackupWorldsChanged(bool value) => PersistSettings();

    /// <summary>Auto-backup interval in minutes (only while a game is running).</summary>
    [ObservableProperty] private int _autoBackupIntervalMinutes = 30;
    partial void OnAutoBackupIntervalMinutesChanged(int value) => PersistSettings();

    /// <summary>Max auto-backup zips to keep per instance (0 = no pruning).</summary>
    [ObservableProperty] private int _autoBackupKeepCount = 10;
    partial void OnAutoBackupKeepCountChanged(int value) => PersistSettings();

    partial void OnFontScaleChanged(double value)
    {
        // Apply globally: set Application FontSize resource.
        try
        {
            if (Avalonia.Application.Current is { } app)
            {
                app.Resources["FontSizeSmall"] = 11.0 * value;
                app.Resources["FontSizeNormal"] = 13.0 * value;
                app.Resources["FontSizeLarge"] = 16.0 * value;
            }
        }
        catch { /* non-fatal */ }
        PersistSettings();
    }

    /// <summary>True when a custom stylesheet is currently active (applied to the live theme).</summary>
    [ObservableProperty] private bool _hasActiveCustomCss;

    /// <summary>Lazy-instantiated CSS manager over the launcher settings dir.</summary>
    private CustomCssManager? _cssManager;
    private CustomCssManager CssManager => _cssManager ??= new CustomCssManager(_settings.SettingsDir);

    [ObservableProperty] private string _newProviderName = string.Empty;
    [ObservableProperty] private string _newProviderUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string _newProviderModel = string.Empty;
    [ObservableProperty] private string _newProviderKey = string.Empty;
    [ObservableProperty] private ChatProviderConfig? _activeProvider;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Active UI theme: "dark", "light", or "system".</summary>
    [ObservableProperty] private string _theme = "dark";

    /// <summary>Custom background image path (PCL-style). Empty = default.</summary>
    [ObservableProperty] private string _backgroundImagePath = string.Empty;

    /// <summary>Custom accent color hex (e.g. "#4fc3f7"). Empty = default blue.</summary>
    [ObservableProperty] private string _accentColor = "#4fc3f7";

    /// <summary>Preset accent colors for quick selection.</summary>
    public IReadOnlyList<string> AccentPresets { get; } = new[]
    {
        "#4fc3f7", // blue
        "#66bb6a", // green
        "#ff7043", // orange
        "#ab47bc", // purple
        "#ef5350", // red
        "#26c6da", // cyan
        "#ffa726", // amber
    };

    partial void OnAccentColorChanged(string value)
    {
        // Apply globally via Avalonia's Application.Current.Resources.
        try
        {
            if (string.IsNullOrEmpty(value)) return;
            var color = Avalonia.Media.Color.Parse(value);
            Avalonia.Application.Current!.Resources["SystemAccentColor"] = color;
            // Also update our shared AccentBrush so custom-styled surfaces follow the user's choice.
            Avalonia.Application.Current!.Resources["AccentBrush"] = new Avalonia.Media.SolidColorBrush(color);
        }
        catch { /* invalid hex — ignore */ }
        // Persist so the accent survives restarts.
        PersistSettings();
        // Live preview: re-derive the swatch + contrast hint for the new accent.
        RaisePreviewChanged();
    }

    partial void OnBackgroundImagePathChanged(string value)
    {
        PersistSettings();
        // Sync to MainWindowVM so the background Image layer updates live (not just on restart).
        var mvm = GetMainWindowVm();
        if (mvm is not null)
            mvm.BackgroundImagePath = string.IsNullOrEmpty(value) ? null : value;
    }

    [ObservableProperty] private string _updateStatus = string.Empty;
    [ObservableProperty] private string _updateUrl = string.Empty;

    /// <summary>True when an update URL is available (drives the release-link button).</summary>
    public bool HasUpdateUrl => !string.IsNullOrEmpty(UpdateUrl);

    partial void OnUpdateUrlChanged(string value) => OnPropertyChanged(nameof(HasUpdateUrl));

    /// <summary>Persist MinecraftPath immediately on change (not just when other commands fire).</summary>
    partial void OnMinecraftPathChanged(string value) => PersistSettings();

    /// <summary>Clamp concurrency into range + persist on change.</summary>
    partial void OnDownloadConcurrencyChanged(int value)
    {
        // Re-clamp if the binding (or a typed-in value) overshot the range.
        if (value < DownloadSettings.MinConcurrency) DownloadConcurrency = DownloadSettings.MinConcurrency;
        else if (value > DownloadSettings.MaxConcurrency) DownloadConcurrency = DownloadSettings.MaxConcurrency;
        else PersistSettings();
    }

    partial void OnDownloadMirrorUrlChanged(string value) => PersistSettings();

    [ObservableProperty] private bool _isCheckingUpdate;
    private readonly UpdateChecker? _updateChecker;
    private readonly NML.Core.Download.IHttpFetcher? _httpFetcher;
    private readonly NML.AICore.Secrets.ISecretStore? _secrets;

    /// <summary>The secret-store key under which the CurseForge API key is persisted (DPAPI-protected).</summary>
    public const string CurseForgeKeySecret = "catalog:curseforge";

    /// <summary>User-entered CurseForge API key (shown masked in the UI; persisted via ISecretStore).</summary>
    [ObservableProperty] private string _curseForgeApiKey = string.Empty;

    /// <summary>Available theme choices for the dropdown.</summary>
    public IReadOnlyList<string> ThemeChoices { get; } = new[] { "dark", "light", "system" };

    partial void OnThemeChanged(string value)
    {
        // Apply the theme globally via Avalonia's RequestedThemeVariant.
        var variant = value switch
        {
            "light" => Avalonia.Styling.ThemeVariant.Light,
            "system" => Avalonia.Styling.ThemeVariant.Default,
            _ => Avalonia.Styling.ThemeVariant.Dark,
        };
        Avalonia.Application.Current!.RequestedThemeVariant = variant;
        // Persist the theme choice so it survives restarts.
        PersistSettings();
        RaisePreviewChanged();
    }

    // --- Custom CSS import: validate + persist + inject the user stylesheet into the live theme.
    // The CSS is persisted to a file, then loaded via a StyleInclude so Avalonia parses it. We keep
    // a single injected-instance reference so re-applying/clearing swaps it cleanly.
    private Avalonia.Markup.Xaml.Styling.StyleInclude? _injectedCss;

    /// <summary>Apply the typed CSS: validate → persist → inject live. Empty/invalid input clears.</summary>
    [RelayCommand]
    private void ApplyCustomCss()
    {
        try
        {
            bool saved = CssManager.Save(CustomCss); // persists + validates; false when rejected/cleared
            RemoveInjectedCustomCss();
            if (saved && CssManager.HasCustomCss())
            {
                // StyleInclude loads + compiles the CSS from the file path at runtime.
                _injectedCss = new Avalonia.Markup.Xaml.Styling.StyleInclude(
                    new System.Uri("avares://NML.App/Styles/custom.css"))
                {
                    Source = new System.Uri(CssManager.FilePath),
                };
                Avalonia.Application.Current?.Styles.Add(_injectedCss);
                HasActiveCustomCss = true;
                Status = "theme.css.applied";
            }
            else
            {
                HasActiveCustomCss = false;
                Status = string.IsNullOrWhiteSpace(CustomCss) ? "theme.css.cleared" : "theme.css.invalid";
            }
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }

    /// <summary>Remove any previously-injected custom-CSS style.</summary>
    private void RemoveInjectedCustomCss()
    {
        if (_injectedCss is null) return;
        try { Avalonia.Application.Current?.Styles.Remove(_injectedCss); }
        catch { /* not present */ }
        _injectedCss = null;
    }

    /// <summary>Clear the persisted stylesheet + remove the injected style.</summary>
    [RelayCommand]
    private void ClearCustomCss()
    {
        CssManager.Clear();
        CustomCss = string.Empty;
        RemoveInjectedCustomCss();
        HasActiveCustomCss = false;
        Status = "theme.css.cleared";
    }

    // --- Live theme preview: derived from Theme + AccentColor so the preview card updates the
    // instant either changes, with no restart. All values come from ThemePreviewModel (tested). ---
    private ThemePreviewModel PreviewModel => new() { Theme = Theme, Accent = AccentColor };

    /// <summary>Hex background for the preview card (light/dark surface per the active theme).</summary>
    public string PreviewBackground => PreviewModel.PreviewBackground;
    /// <summary>Hex foreground text color for the preview card.</summary>
    public string PreviewForeground => PreviewModel.PreviewForeground;
    /// <summary>The accent to actually swatch (falls back to default on invalid hex).</summary>
    public string PreviewAccent => PreviewModel.EffectiveAccent;
    /// <summary>True when the typed accent parses to a valid hex (drives a validation indicator).</summary>
    public bool IsAccentValid => PreviewModel.IsAccentValid;
    /// <summary>Read-on-accent text color (white on dark accents, black on light accents).</summary>
    public string AccentOnColor => PreviewModel.AccentOnColor;
    /// <summary>Human-readable preview header describing the current selection.</summary>
    public string PreviewSampleText => PreviewModel.SampleText;

    /// <summary>Re-raise every preview-derived property so the live card re-renders immediately.</summary>
    private void RaisePreviewChanged()
    {
        OnPropertyChanged(nameof(PreviewBackground));
        OnPropertyChanged(nameof(PreviewForeground));
        OnPropertyChanged(nameof(PreviewAccent));
        OnPropertyChanged(nameof(IsAccentValid));
        OnPropertyChanged(nameof(AccentOnColor));
        OnPropertyChanged(nameof(PreviewSampleText));
    }

    /// <summary>Resolved lazily to avoid circular DI (MainWindowVM depends on SettingsPageVM).</summary>
    private MainWindowViewModel? _mainWindowVm;

    private MainWindowViewModel? GetMainWindowVm() =>
        _mainWindowVm ??= Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.DataContext as MainWindowViewModel
                : null;

    public SettingsPageViewModel(
        SettingsStore settings,
        LocalModelProbe probe,
        ChatClientFactory factory,
        JavaRuntimeDetector javaDetector,
        ILogger<SettingsPageViewModel> logger,
        UpdateChecker? updateChecker = null,
        NML.Core.Java.JavaRuntimeInstaller? javaInstaller = null,
        NML.Core.Download.IHttpFetcher? httpFetcher = null,
        NML.AICore.Secrets.ISecretStore? secretStore = null)
    {
        _settings = settings;
        _probe = probe;
        _factory = factory;
        _javaDetector = javaDetector;
        _logger = logger;
        _updateChecker = updateChecker;
        _javaInstaller = javaInstaller;
        _httpFetcher = httpFetcher;
        _secrets = secretStore;
        EnsureLanguageSubscribed();

        // Populate the language picker from the registered cultures.
        foreach (CultureInfo c in LocalizationService.Instance.AvailableCultures) AvailableLanguages.Add(c);

        LauncherSettings s = settings.Load();
        MinecraftPath = s.MinecraftRoot ?? string.Empty;
        DownloadConcurrency = s.DownloadConcurrency ?? DownloadSettings.DefaultConcurrency;
        DownloadMirrorUrl = s.DownloadMirrorUrl ?? string.Empty;
        FontScale = s.FontScale ?? 1.0;
        CheckForUpdatesOnStartup = s.CheckForUpdatesOnStartup ?? true;
        ProxyUrl = s.ProxyUrl ?? string.Empty;
        AutoBackupWorlds = s.AutoBackupWorlds ?? false;
        AutoBackupIntervalMinutes = s.AutoBackupIntervalMinutes ?? 30;
        AutoBackupKeepCount = s.AutoBackupKeepCount ?? 10;

        // Load the saved CurseForge API key from the secret store (DPAPI-protected), if any.
        try { CurseForgeApiKey = _secrets?.GetAsync(CurseForgeKeySecret).GetAwaiter().GetResult() ?? string.Empty; }
        catch { CurseForgeApiKey = string.Empty; }
        BackgroundImagePath = s.BackgroundImagePath ?? string.Empty;
        AccentColor = s.AccentColor ?? "#4fc3f7";
        Theme = s.Theme ?? "dark";
        // Load any persisted custom CSS into the editor and apply it live at startup.
        CustomCss = CssManager.Load() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(CustomCss)) ApplyCustomCss();
        foreach (ChatProviderConfig p in s.Providers) Providers.Add(p);
        SelectedLanguage = AvailableLanguages.FirstOrDefault(c =>
            c.Name.Equals(LocalizationService.Instance.CurrentCulture.Name, StringComparison.OrdinalIgnoreCase));
        ActiveProvider = Providers.FirstOrDefault(p => p.Name == s.ActiveProviderName);
    }

    /// <summary>Live-apply language when the user picks one.</summary>
    partial void OnSelectedLanguageChanged(CultureInfo? value)
    {
        if (value is not null)
        {
            LocalizationService.Instance.CurrentCulture = value;
            // Persist the resolved culture key to language.txt so it round-trips on next startup.
            string resolvedKey = LocalizationService.Instance.ResolveCultureKey(value.Name) ?? value.Name;
            string langPath = Path.Combine(_settings.SettingsDir, "language.txt");
            File.WriteAllText(langPath, resolvedKey);
            // Persist all settings atomically via the shared path.
            PersistSettings();
        }
    }

    public override Task OnNavigatedToAsync()
    {
        if (JavaRuntimes.Count == 0) DetectJava();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void DetectJava()
    {
        JavaRuntimes.Clear();
        foreach (JavaRuntime j in _javaDetector.DetectAll()) JavaRuntimes.Add(j);
        Status = JavaRuntimes.Count > 0 ? $"{JavaRuntimes.Count}" : "common.error";
    }

    [RelayCommand]
    private async Task DetectLocalModelsAsync()
    {
        Status = "common.loading";
        IReadOnlyList<ChatProviderConfig> found = await _probe.DetectAsync();
        foreach (ChatProviderConfig p in found)
            if (!Providers.Any(x => x.BaseUrl == p.BaseUrl)) Providers.Add(p);
        Status = found.Count > 0 ? $"settings.local_detected,{found.Count}" : "settings.local_none";
    }

    [RelayCommand]
    private void AddCloudProvider()
    {
        if (string.IsNullOrWhiteSpace(NewProviderName) ||
            string.IsNullOrWhiteSpace(NewProviderModel) ||
            string.IsNullOrWhiteSpace(NewProviderKey))
        { Status = "common.error"; return; }

        var cfg = new ChatProviderConfig
        {
            Kind = ChatProviderKind.OpenAiCompatible,
            Name = NewProviderName, BaseUrl = NewProviderUrl,
            Model = NewProviderModel, ApiKey = NewProviderKey,
        };
        try { _factory.Create(cfg); }
        catch (ArgumentException ex) { Status = $"common.error,{ex.Message}"; return; }

        Providers.Add(cfg.With());
        PersistSettings();
        NewProviderName = NewProviderModel = NewProviderKey = string.Empty;
        Status = $"home.installed,{cfg.Name}";
    }

    [RelayCommand]
    private void Activate(ChatProviderConfig provider)
    {
        ActiveProvider = provider;
        PersistSettings();
        Status = $"settings.ai_active_provider,{provider.Name}";
    }

    private void PersistSettings()
    {
        var s = _settings.Load();
        s.Providers = Providers.ToList();
        s.ActiveProviderName = ActiveProvider?.Name;
        s.MinecraftRoot = MinecraftPath;
        s.DownloadConcurrency = DownloadSettings.Clamp(DownloadConcurrency);
        s.DownloadMirrorUrl = string.IsNullOrEmpty(DownloadMirrorUrl) ? null : DownloadMirrorUrl;
        s.FontScale = FontScale;
        s.CheckForUpdatesOnStartup = CheckForUpdatesOnStartup;
        s.ProxyUrl = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl.Trim();
        s.AutoBackupWorlds = AutoBackupWorlds;
        s.AutoBackupIntervalMinutes = AutoBackupIntervalMinutes;
        s.AutoBackupKeepCount = AutoBackupKeepCount;
        s.BackgroundImagePath = string.IsNullOrEmpty(BackgroundImagePath) ? null : BackgroundImagePath;
        s.AccentColor = string.IsNullOrEmpty(AccentColor) ? null : AccentColor;
        s.Theme = Theme;
        _settings.Save(s);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateChecker is null) { UpdateStatus = "common.error"; return; }
        IsCheckingUpdate = true;
        UpdateStatus = "common.loading";
        try
        {
            // Read the actual running version from the assembly, not a hardcoded string.
            string currentVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "0.1.0";
            var info = await _updateChecker.CheckAsync(currentVersion);
            if (info is null)
            {
                UpdateStatus = "update.check_failed";
                UpdateUrl = string.Empty;
            }
            else if (!info.IsNewer)
            {
                UpdateStatus = "update.up_to_date";
                UpdateUrl = string.Empty;
            }
            else
            {
                UpdateStatus = $"update.available,{info.TagName}";
                UpdateUrl = info.HtmlUrl;
                // Remember the asset URL so DownloadUpdate can fetch the binary directly (if any).
                LatestUpdateInfo = info;
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"common.error,{ex.Message}";
            _logger.LogError(ex, "Update check failed.");
        }
        finally { IsCheckingUpdate = false; }
    }

    /// <summary>The most recent update-info (when an update is available). Drives DownloadUpdate.</summary>
    [ObservableProperty] private NML.Core.Update.UpdateInfo? _latestUpdateInfo;

    /// <summary>True when the available update ships a downloadable asset (exe/zip).</summary>
    public bool HasUpdateAsset => LatestUpdateInfo?.Assets.Count > 0;
    partial void OnLatestUpdateInfoChanged(NML.Core.Update.UpdateInfo? value) => OnPropertyChanged(nameof(HasUpdateAsset));

    /// <summary>
    /// Download the update's binary asset to the user's Downloads folder, then open that folder so
    /// they can run the installer/replace the exe. Falls back to opening the release web page when no
    /// asset is present. Non-fatal on any error (network/disk) — surfaced via Status.
    /// </summary>
    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (LatestUpdateInfo is null) return;
        try
        {
            // Prefer an exe/zip asset; fall back to opening the release page.
            var asset = LatestUpdateInfo.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?? LatestUpdateInfo.Assets.FirstOrDefault();
            if (asset is null)
            {
                OpenUpdateUrl(LatestUpdateInfo.HtmlUrl);
                return;
            }

            string downloads = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(downloads)) downloads = System.IO.Path.GetTempPath();
            string dest = System.IO.Path.Combine(downloads, asset.Name);
            Status = $"update.downloading,{asset.Name}";
            if (_httpFetcher is not null)
            {
                await _httpFetcher.StreamToAsync(asset.Url, System.IO.File.Create(dest), null);
            }

            // If it's an exe asset and we're a single-file publish, offer in-place apply:
            // write an updater script that waits for us to exit, swaps the exe, and relaunches.
            if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                System.OperatingSystem.IsWindows() &&
                TryBeginSelfUpdate(dest, out string updaterScript))
            {
                Status = $"update.applying,{asset.Name}";
                // Give the status a moment to render, then exit — the updater takes over.
                await System.Threading.Tasks.Task.Delay(1200);
                System.Environment.Exit(0);
            }

            OpenInExplorer(dest);
            Status = $"update.downloaded,{dest}";
        }
        catch (Exception ex)
        {
            Status = $"common.error,{ex.Message}";
            _logger.LogWarning(ex, "Update download failed.");
        }
    }

    /// <summary>
    /// Write a self-update script (Windows: .bat) that waits for the current process to exit,
    /// replaces the running exe with <paramref name="newExe"/>, and relaunches it. Returns true
    /// when the script was written and started; the caller should then exit the launcher.
    /// </summary>
    private bool TryBeginSelfUpdate(string newExe, out string updaterScript)
    {
        updaterScript = string.Empty;
        try
        {
            string? currentExe = System.Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe)) return false;

            updaterScript = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nml-update-{DateTime.Now:yyyyMMddHHmmss}.bat");
            string batch =
                "@echo off\r\n" +
                "rem NML self-updater: wait for the launcher to exit, swap the exe, relaunch.\r\n" +
                $"timeout /t 2 /nobreak >nul\r\n" +
                ":waitloop\r\n" +
                "tasklist /FI \"PID EQ " + System.Diagnostics.Process.GetCurrentProcess().Id + "\" | find /I \"" +
                System.IO.Path.GetFileNameWithoutExtension(currentExe) + "\" >nul\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto waitloop\r\n" +
                ")\r\n" +
                $"copy /Y \"{newExe}\" \"{currentExe}\" >nul\r\n" +
                $"start \"\" \"{currentExe}\"\r\n" +
                $"del \"%~f0\"\r\n";
            File.WriteAllText(updaterScript, batch);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(updaterScript)
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-update script failed; falling back to manual replace.");
            return false;
        }
    }

    private static void OpenUpdateUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (System.OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    System.IO.Path.GetDirectoryName(path) ?? path) { UseShellExecute = true });
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Persist the entered CurseForge API key to the secret store (DPAPI-protected on Windows) so the
    /// CurseForge mod catalog becomes functional. After saving, the next launcher restart wires the
    /// key into the catalog via DI. Empty input clears the stored key (disables CurseForge).
    /// </summary>
    [RelayCommand]
    private async Task SaveCurseForgeKeyAsync()
    {
        if (_secrets is null) { Status = "common.error"; return; }
        try
        {
            string key = CurseForgeApiKey?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(key))
            {
                await _secrets.DeleteAsync(CurseForgeKeySecret);
                Status = "curseforge.key_cleared";
            }
            else
            {
                await _secrets.SetAsync(CurseForgeKeySecret, key);
                Status = "curseforge.key_saved";
            }
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }

    /// <summary>True when a non-empty CurseForge key is entered (drives the "saved" indicator).</summary>
    public bool HasCurseForgeKey => !string.IsNullOrWhiteSpace(CurseForgeApiKey);
    partial void OnCurseForgeApiKeyChanged(string value) => OnPropertyChanged(nameof(HasCurseForgeKey));

    /// <summary>
    /// Export the current look (theme variant + accent + font scale + custom CSS) as a portable
    /// JSON preset on the desktop, so it can be backed up or shared (HMCL-style theme files).
    /// </summary>
    [RelayCommand]
    private void ExportThemePreset()
    {
        try
        {
            string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = System.IO.Path.Combine(desktop, $"nml-theme-{stamp}.json");
            var preset = new NML.Core.Theming.ThemePreset
            {
                Name = $"Theme {stamp}",
                Theme = Theme,
                Accent = AccentColor,
                FontScale = FontScale,
                CustomCss = CustomCss,
            };
            NML.Core.Theming.ThemePresetSerializer.Export(preset, path);
            Status = $"Theme exported: {path}";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }

    /// <summary>Open an OS file-picker to choose a theme preset JSON, then apply it live.</summary>
    [RelayCommand]
    private async Task ImportThemePresetAsync()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is null) return;
            var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import theme preset",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Theme preset") { Patterns = new[] { "*.json" } },
                },
            });
            if (files.Count == 0) return;

            var preset = NML.Core.Theming.ThemePresetSerializer.Import(files[0].Path.LocalPath);
            // Apply the imported look live (mirrors the per-field setters).
            Theme = preset.Theme;
            AccentColor = preset.Accent;
            FontScale = preset.FontScale;
            if (!string.IsNullOrWhiteSpace(preset.CustomCss))
            {
                CustomCss = preset.CustomCss;
                ApplyCustomCss();
            }
            PersistSettings();
            Status = $"Theme imported: {preset.Name}";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }
}

internal static class ProviderRecordCopy
{
    // `cfg with { }` only works on records; ChatProviderConfig is a class. Provide a copy helper.
    public static ChatProviderConfig With(this ChatProviderConfig p) => new()
    {
        Kind = p.Kind, Name = p.Name, BaseUrl = p.BaseUrl, Model = p.Model, ApiKey = p.ApiKey,
        Temperature = p.Temperature, MaxOutputTokens = p.MaxOutputTokens,
    };
}
