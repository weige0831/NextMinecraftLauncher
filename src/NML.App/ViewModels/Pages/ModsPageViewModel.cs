using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NML.AICore.Features;
using NML.App.Services;
using NML.Core.Instances;
using NML.Data;
using NML.Data.Modrinth;

namespace NML.App.ViewModels.Pages;
public partial class ModsPageViewModel : PageViewModelBase
{
    public override string TitleKey => "nav.mods";
    public override string Icon => "▦";

    private readonly ModrinthCatalog _catalog;
    private readonly ModRecommenderFactory _recommenderFactory;
    private readonly InstanceStore _instances;
    private readonly NML.Core.Download.Downloader _downloader;
    private readonly ILogger<ModsPageViewModel> _logger;
    private readonly NML.Data.CurseForge.CurseForgeCatalog? _curseForge;

    /// <summary>Available mod sources for the dropdown.</summary>
    public IReadOnlyList<string> AvailableSources { get; } = new[] { "Modrinth", "CurseForge" };

    [ObservableProperty] private string _selectedSource = "Modrinth";

    public ObservableCollection<ModSearchResult> Results { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _recommendPrompt = string.Empty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isRecommending;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _installingModId = string.Empty;

    public ModsPageViewModel(
        ModrinthCatalog catalog,
        ModRecommenderFactory recommenderFactory,
        InstanceStore instances,
        NML.Core.Download.Downloader downloader,
        ILogger<ModsPageViewModel> logger,
        NML.Data.CurseForge.CurseForgeCatalog? curseForge = null)
    {
        _catalog = catalog;
        _recommenderFactory = recommenderFactory;
        _instances = instances;
        _downloader = downloader;
        _logger = logger;
        _curseForge = curseForge;
        EnsureLanguageSubscribed();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        IsSearching = true;
        Results.Clear();
        Status = "common.loading";
        try
        {
            IModCatalog catalog = SelectedSource == "CurseForge" && _curseForge is not null
                ? _curseForge
                : _catalog;
            IReadOnlyList<ModSearchResult> r = await catalog.SearchAsync(SearchText.Trim());
            foreach (ModSearchResult m in r) Results.Add(m);
            Status = r.Count > 0 ? $"mods.results,{r.Count}" : "mods.no_results";
        }
        catch (Exception ex)
        {
            Status = $"common.error,{ex.Message}";
            _logger.LogError(ex, "Mod search failed.");
        }
        finally { IsSearching = false; }
    }

    [RelayCommand]
    private async Task RecommendAsync()
    {
        ModRecommender? recommender = _recommenderFactory.TryCreate();
        if (recommender is null || string.IsNullOrWhiteSpace(RecommendPrompt))
        {
            Status = "assistant.no_provider";
            return;
        }
        IsRecommending = true;
        Results.Clear();
        Status = "assistant.thinking";
        try
        {
            IReadOnlyList<ModRecommendation> recs =
                await recommender.RecommendAsync(_catalog, RecommendPrompt.Trim());
            foreach (ModRecommendation r in recs) Results.Add(r.Mod);
            Status = recs.Count > 0 ? $"mods.results,{recs.Count}" : "mods.no_results";
        }
        catch (Exception ex)
        {
            Status = $"common.error,{ex.Message}";
            _logger.LogError(ex, "AI recommendation failed.");
        }
        finally { IsRecommending = false; }
    }

    /// <summary>Install a mod from a search result: fetch the latest version file, download to mods/.</summary>
    [RelayCommand]
    private async Task InstallModAsync(ModSearchResult mod)
    {
        if (mod is null) return;

        // Need an instance to install into.
        Instance? inst = _instances.LoadAll().FirstOrDefault();
        if (inst is null)
        {
            Status = "mods.no_instance";
            return;
        }

        string gameDir = _instances.GameDirFor(inst);
        string modsDir = System.IO.Path.Combine(gameDir, "mods");
        System.IO.Directory.CreateDirectory(modsDir);

        IsInstalling = true;
        InstallingModId = mod.ProjectId;
        Status = "mods.installing";
        try
        {
            // Fetch the mod's version files for the instance's MC version.
            IModCatalog catalog = SelectedSource == "CurseForge" && _curseForge is not null
                ? _curseForge
                : _catalog;
            ModLoader loader = inst.Modloader?.ToLowerInvariant() switch
            {
                "fabric" => ModLoader.Fabric,
                "forge" => ModLoader.Forge,
                "quilt" => ModLoader.Quilt,
                "neoforge" => ModLoader.NeoForge,
                _ => ModLoader.Any,
            };
            var files = loader != ModLoader.Any
                ? await catalog.GetFilesAsync(mod.ProjectId, inst.VersionId, loader)
                : Array.Empty<ModFile>();
            if (files.Count == 0)
            {
                files = await catalog.GetFilesAsync(mod.ProjectId, inst.VersionId, NML.Data.ModLoader.Any);
            }
            if (files.Count == 0)
            {
                Status = "mods.no_files";
                return;
            }

            // Download the first matching file into mods/.
            var file = files[0];
            string destPath = System.IO.Path.Combine(modsDir, file.FileName);
            // Use the downloader's SHA-1 verified download path.
            var downloadable = new NML.Core.Models.Downloadable
            {
                Url = file.DownloadUrl,
                Sha1 = file.Sha1 ?? "",
                Size = file.Size,
            };
            await _downloader.DownloadAsync(downloadable, file.FileName, modsDir);
            Status = $"mods.installed,{mod.Title}";
        }
        catch (Exception ex)
        {
            Status = $"common.error,{ex.Message}";
            _logger.LogError(ex, "Mod install failed for {Id}.", mod.ProjectId);
        }
        finally
        {
            IsInstalling = false;
            InstallingModId = string.Empty;
        }
    }
}
