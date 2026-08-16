using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NML.AICore;
using NML.AICore.LocalModels;
using NML.AICore.Secrets;
using NML.App.Services;

namespace NML.App.ViewModels;

/// <summary>
/// View model for the AI settings page: shows configured providers, lets the user add a
/// cloud provider (name/url/model/key), auto-detects local model servers, and selects the
/// active provider. API keys are stored encrypted via <see cref="ISecretStore"/>.
/// </summary>
public partial class AiSettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly ChatClientFactory _factory;
    private readonly LocalModelProbe _probe;
    private readonly ISecretStore _secrets;
    private readonly ILogger<AiSettingsViewModel> _logger;

    public ObservableCollection<ChatProviderConfig> Providers { get; } = new();

    [ObservableProperty] private string _newProviderName = string.Empty;
    [ObservableProperty] private string _newProviderUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string _newProviderModel = string.Empty;
    [ObservableProperty] private string _newProviderKey = string.Empty;
    [ObservableProperty] private bool _isLocalServerDetected;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private ChatProviderConfig? _activeProvider;

    public AiSettingsViewModel(
        SettingsStore settings,
        ChatClientFactory factory,
        LocalModelProbe probe,
        ISecretStore secrets,
        ILogger<AiSettingsViewModel> logger)
    {
        _settings = settings;
        _factory = factory;
        _probe = probe;
        _secrets = secrets;
        _logger = logger;

        ReloadFromDisk();
    }

    /// <summary>Re-load providers from settings (and attach any stored API keys).</summary>
    public async void ReloadFromDisk()
    {
        LauncherSettings s = _settings.Load();
        Providers.Clear();
        foreach (ChatProviderConfig cfg in s.Providers)
        {
            // Re-attach the API key from the secret store (settings.json stores no secrets).
            if (cfg.Kind != ChatProviderKind.Local)
            {
                string? key = await _secrets.GetAsync($"provider:{cfg.Name}");
                Providers.Add(cfg.WithApiKey(key));
            }
            else
            {
                Providers.Add(cfg);
            }
        }
        ActiveProvider = Providers.FirstOrDefault(p => p.Name == s.ActiveProviderName);
    }

    /// <summary>Probe localhost for Ollama/LM Studio and add them as providers if found.</summary>
    [RelayCommand]
    private async Task DetectLocalModelsAsync()
    {
        StatusMessage = "Scanning localhost for model servers…";
        IReadOnlyList<ChatProviderConfig> detected = await _probe.DetectAsync();
        foreach (ChatProviderConfig d in detected)
        {
            if (!Providers.Any(p => p.BaseUrl == d.BaseUrl))
                Providers.Add(d);
        }
        IsLocalServerDetected = detected.Count > 0;
        StatusMessage = detected.Count > 0
            ? $"Found {detected.Count} local server(s): {string.Join(", ", detected.Select(d => d.Name))}."
            : "No local model server found. Install Ollama/LM Studio or add a cloud provider.";
    }

    /// <summary>Add the current cloud-provider form values as a configured provider.</summary>
    [RelayCommand]
    private async Task AddCloudProviderAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProviderName) ||
            string.IsNullOrWhiteSpace(NewProviderModel) ||
            string.IsNullOrWhiteSpace(NewProviderKey))
        {
            StatusMessage = "Name, model and API key are required.";
            return;
        }

        var cfg = new ChatProviderConfig
        {
            Kind = ChatProviderKind.OpenAiCompatible,
            Name = NewProviderName,
            BaseUrl = NewProviderUrl,
            Model = NewProviderModel,
            ApiKey = NewProviderKey,
        };

        // Validate the client can be built before persisting.
        try { _factory.Create(cfg); }
        catch (ArgumentException ex)
        {
            StatusMessage = $"Invalid config: {ex.Message}";
            return;
        }

        // Store the key encrypted, persist the keyless config.
        await _secrets.SetAsync($"provider:{cfg.Name}", cfg.ApiKey);
        Providers.Add(cfg.WithApiKey(null));
        PersistSettings();

        NewProviderName = NewProviderModel = NewProviderKey = string.Empty;
        StatusMessage = $"Added provider '{cfg.Name}'. Select it to activate.";
    }

    /// <summary>Activate the given provider (sets the active AI backend).</summary>
    [RelayCommand]
    private void Activate(ChatProviderConfig provider)
    {
        ActiveProvider = provider;
        PersistSettings();
        StatusMessage = $"Active AI provider: {provider.Name} ({provider.Model}).";
    }

    private void PersistSettings()
    {
        LauncherSettings s = _settings.Load();
        // SECURITY: strip API keys before persisting — settings.json is plaintext; the real
        // keys live only in the DPAPI secret store (re-attached on load by ReloadFromDisk).
        s.Providers = Providers.Select(p => p.WithApiKey(null)).ToList();
        s.ActiveProviderName = ActiveProvider?.Name;
        _settings.Save(s);
    }
}

/// <summary>Internal helper to set an API key on a record-style copy without exposing the ctor.</summary>
internal static class ProviderCopyExtensions
{
    public static ChatProviderConfig WithApiKey(this ChatProviderConfig p, string? key) =>
        new()
        {
            Kind = p.Kind,
            Name = p.Name,
            BaseUrl = p.BaseUrl,
            Model = p.Model,
            ApiKey = key,
            Temperature = p.Temperature,
            MaxOutputTokens = p.MaxOutputTokens,
        };
}
