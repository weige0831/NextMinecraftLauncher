using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NML.App.Services;
using NML.Core;
using NML.Core.Download;
using NML.Core.Instances;
using NML.Core.Models;

namespace NML.App.ViewModels.Pages;

/// <summary>
/// Download-center page: lists the full Mojang version manifest, with search and type
/// filtering (release/snapshot/old). Installing a version creates an Instance and runs
/// the vanilla installer end-to-end.
/// </summary>
public partial class DownloadPageViewModel : PageViewModelBase
{
    public override string TitleKey => "nav.download";
    public override string Icon => "↓";

    private readonly VersionManifestService _manifest;
    private readonly VanillaInstaller _vanillaInstaller;
    private readonly VersionInfoService _versions;
    private readonly InstanceStore _instances;
    private readonly SettingsStore _settings;
    private readonly ILogger<DownloadPageViewModel> _logger;
    private readonly Core.Modloaders.FabricInstaller? _fabricInstaller;
    private readonly Core.Modloaders.QuiltInstaller? _quiltInstaller;
    private readonly Core.Modloaders.ForgeInstaller? _forgeInstaller;
    private readonly Core.Modloaders.NeoForgeInstaller? _neoForgeInstaller;
    private readonly Core.Modloaders.OptiFineInstaller? _optifineInstaller;
    private readonly Core.Modloaders.LiteLoaderInstaller? _liteloaderInstaller;
    private readonly Core.Java.JavaRuntimeDetector? _javaDetector;

    private IReadOnlyList<VersionManifestEntry> _all = Array.Empty<VersionManifestEntry>();

    /// <summary>Currently displayed (filtered) versions.</summary>
    public ObservableCollection<VersionManifestEntry> FilteredVersions { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _typeFilter = "release"; // release|snapshot|old_beta|all
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _installingVersion = string.Empty;
    [ObservableProperty] private int _installProgress;

    /// <summary>Selected version to install a modloader against (HMCL-style modloader install section).</summary>
    [ObservableProperty] private VersionManifestEntry? _selectedModloaderVersion;
    /// <summary>Selected modloader type for the modloader install panel.</summary>
    [ObservableProperty] private string _selectedModloader = "None";

    /// <summary>Modloader choices for the install panel (None + the loaders wired into this VM).</summary>
    public IReadOnlyList<string> ModloaderChoices { get; } = new[] { "None", "Fabric", "Quilt", "Forge", "NeoForge", "OptiFine", "LiteLoader" };

    /// <summary>True when an install is in progress (drives progress bar visibility).</summary>
    public bool IsInstalling => !string.IsNullOrEmpty(InstallingVersion);

    partial void OnInstallingVersionChanged(string value) => OnPropertyChanged(nameof(IsInstalling));

    public DownloadPageViewModel(
        VersionManifestService manifest,
        VanillaInstaller vanillaInstaller,
        VersionInfoService versions,
        InstanceStore instances,
        SettingsStore settings,
        ILogger<DownloadPageViewModel> logger,
        Core.Modloaders.FabricInstaller? fabricInstaller = null,
        Core.Modloaders.QuiltInstaller? quiltInstaller = null,
        Core.Modloaders.ForgeInstaller? forgeInstaller = null,
        Core.Modloaders.NeoForgeInstaller? neoForgeInstaller = null,
        Core.Modloaders.OptiFineInstaller? optifineInstaller = null,
        Core.Modloaders.LiteLoaderInstaller? liteloaderInstaller = null,
        Core.Java.JavaRuntimeDetector? javaDetector = null)
    {
        _manifest = manifest;
        _vanillaInstaller = vanillaInstaller;
        _versions = versions;
        _instances = instances;
        _settings = settings;
        _logger = logger;
        _fabricInstaller = fabricInstaller;
        _quiltInstaller = quiltInstaller;
        _forgeInstaller = forgeInstaller;
        _neoForgeInstaller = neoForgeInstaller;
        _optifineInstaller = optifineInstaller;
        _liteloaderInstaller = liteloaderInstaller;
        _javaDetector = javaDetector;
        EnsureLanguageSubscribed();
    }

    public override async Task OnNavigatedToAsync()
    {
        if (_all.Count == 0) await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        Status = "download.loading";
        try
        {
            VersionManifest m = await _manifest.GetAsync();
            _all = m.Versions;
            ApplyFilter();
            Status = $"download.results,{_all.Count}";
        }
        catch (Exception ex)
        {
            // Distinguish network errors (show a friendly localized message) from other errors.
            if (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException)
                Status = "download.network_error";
            else
                Status = $"download.load_failed,{ex.Message}";
            _logger.LogError(ex, "Version manifest load failed.");
        }
        finally { IsLoading = false; }
    }

    /// <summary>Re-apply the search/type filter to the cached full list.</summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnTypeFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredVersions.Clear();
        IEnumerable<VersionManifestEntry> src = _all;

        if (TypeFilter != "all")
        {
            // "all" shows everything; otherwise filter by exact Mojang type.
            // (Releases = "release", Snapshots = "snapshot", Old = "old_beta" + "old_alpha".)
            src = TypeFilter switch
            {
                "old_beta" => src.Where(v => v.Type == "old_beta" || v.Type == "old_alpha"),
                _ => src.Where(v => v.Type == TypeFilter),
            };
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string q = SearchText.Trim();
            src = src.Where(v => v.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (VersionManifestEntry v in src.Take(200)) FilteredVersions.Add(v);
    }

    [RelayCommand]
    private async Task InstallAsync(VersionManifestEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id)) return;
        string versionId = entry.Id;
        string name = $"{versionId} (vanilla)";
        var instance = new Instance { Name = name, VersionId = versionId, MaxMemoryMb = 4096 };
        var mc = new MinecraftDirectory(_instances.GameDirFor(name));

        InstallingVersion = versionId;
        InstallProgress = 0;
        try
        {
            await _vanillaInstaller.InstallAsync(versionId, mc,
                downloadSettings: _settings.ResolveDownloadSettings(_manifest),
                progress: (in DownloadProgress p, string f) =>
                {
                    if (p.TotalFiles > 0) InstallProgress = (int)(p.FileFraction * 100);
                });
            _instances.Add(instance);
            Status = $"home.installed,{versionId}";
        }
        catch (Exception ex)
        {
            Status = $"home.install_failed,{ex.Message}";
            _logger.LogError(ex, "Install of {Id} failed.", versionId);
        }
        finally { InstallingVersion = string.Empty; }
    }

    /// <summary>
    /// Install a modloader (Fabric/Quilt/Forge/NeoForge) against the selected game version — the
    /// HMCL-style "download modloader" panel. Installs vanilla first (so the loader has a parent to
    /// hook into), then the loader, and registers an instance named "{version} ({loader})".
    /// </summary>
    [RelayCommand]
    private async Task InstallModloaderAsync()
    {
        if (SelectedModloaderVersion is null) { Status = "download.pick_version"; return; }
        if (SelectedModloader == "None") { Status = "download.pick_loader"; return; }

        string versionId = SelectedModloaderVersion.Id;
        string loader = SelectedModloader;
        string name = $"{versionId} ({loader})";
        var instance = new Instance { Name = name, VersionId = versionId, MaxMemoryMb = 4096, Modloader = loader };
        var mc = new MinecraftDirectory(_instances.GameDirFor(name));

        InstallingVersion = $"{versionId}+{loader}";
        InstallProgress = 0;
        Status = $"download.installing_loader,{loader},{versionId}";
        try
        {
            // Vanilla first (parent the loader inherits from).
            await _vanillaInstaller.InstallAsync(versionId, mc,
                downloadSettings: _settings.ResolveDownloadSettings(_manifest));

            string? profileId = loader switch
            {
                "Fabric" => await InstallFabricAsync(versionId, mc),
                "Quilt" => await InstallQuiltAsync(versionId, mc),
                "Forge" => await InstallForgeAsync(versionId, mc),
                "NeoForge" => await InstallNeoForgeAsync(versionId, mc),
                "OptiFine" => await InstallOptiFineAsync(versionId, mc),
                "LiteLoader" => await InstallLiteLoaderAsync(versionId, mc),
                _ => null,
            };
            if (profileId is null)
            {
                Status = $"download.loader_unavailable,{loader}";
                return;
            }
            _instances.Add(instance);
            Status = $"download.loader_installed,{loader},{versionId}";
        }
        catch (Exception ex)
        {
            Status = $"home.install_failed,{loader}: {ex.Message}";
            _logger.LogError(ex, "Modloader {Loader} install failed for {Id}.", loader, versionId);
        }
        finally { InstallingVersion = string.Empty; }
    }

    // --- Per-loader install helpers (mirror HomePageViewModel's pattern) ---

    private async Task<string?> InstallFabricAsync(string versionId, MinecraftDirectory mc)
    {
        if (_fabricInstaller is null) return null;
        var loaders = await _fabricInstaller.ListLoadersAsync(versionId);
        var stable = loaders.FirstOrDefault(l => l.IsStable) ?? loaders.FirstOrDefault();
        return stable is null ? null : await _fabricInstaller.InstallAsync(versionId, stable.LoaderVersion, mc);
    }

    private async Task<string?> InstallQuiltAsync(string versionId, MinecraftDirectory mc)
    {
        if (_quiltInstaller is null) return null;
        var loaders = await _quiltInstaller.ListLoadersAsync(versionId);
        var stable = loaders.FirstOrDefault(l => l.IsStable) ?? loaders.FirstOrDefault();
        return stable is null ? null : await _quiltInstaller.InstallAsync(versionId, stable.LoaderVersion, mc);
    }

    private async Task<string?> InstallForgeAsync(string versionId, MinecraftDirectory mc)
    {
        if (_forgeInstaller is null) return null;
        var versions = await _forgeInstaller.ListVersionsAsync(versionId);
        var latest = versions.FirstOrDefault();
        return latest is null ? null : await _forgeInstaller.InstallAsync(versionId, latest.LoaderVersion, mc);
    }

    private async Task<string?> InstallNeoForgeAsync(string versionId, MinecraftDirectory mc)
    {
        if (_neoForgeInstaller is null) return null;
        var versions = await _neoForgeInstaller.ListVersionsAsync(versionId);
        var latest = versions.FirstOrDefault();
        return latest is null ? null : await _neoForgeInstaller.InstallAsync(versionId, latest.LoaderVersion, mc);
    }

    private async Task<string?> InstallOptiFineAsync(string versionId, MinecraftDirectory mc)
    {
        if (_optifineInstaller is null || _javaDetector is null) return null;
        var versions = await _optifineInstaller.ListVersionsAsync(versionId);
        var latest = versions.FirstOrDefault();
        if (latest is null) return null;
        var runtimes = _javaDetector.DetectAll();
        var java = runtimes.FirstOrDefault() ?? _javaDetector.FindForVersion(17, runtimes);
        if (java is null) return null;
        string installerCacheDir = System.IO.Path.Combine(mc.Root, "cache", "optifine");
        return await _optifineInstaller.InstallAsync(versionId, latest.Type, latest.Patch,
            installerCacheDir, java.ExecutablePath, mc);
    }

    private async Task<string?> InstallLiteLoaderAsync(string versionId, MinecraftDirectory mc)
    {
        if (_liteloaderInstaller is null) return null;
        var versions = await _liteloaderInstaller.ListVersionsAsync(versionId);
        var latest = versions.FirstOrDefault();
        return latest is null ? null : await _liteloaderInstaller.InstallAsync(latest, mc);
    }
}
