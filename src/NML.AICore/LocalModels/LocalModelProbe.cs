using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NML.AICore.LocalModels;

/// <summary>
/// Probes the local network for running model servers (Ollama on :11434, LM Studio on :1234).
/// If found, returns ready-to-use <see cref="ChatProviderConfig"/>s so the user gets a
/// zero-key AI experience out of the box.
/// </summary>
public sealed class LocalModelProbe
{
    private static readonly (string Name, string BaseUrl, ChatProviderKind Kind)[] Candidates =
    {
        ("Ollama (local)", "http://localhost:11434/v1", ChatProviderKind.Local),
        ("LM Studio (local)", "http://localhost:1234/v1", ChatProviderKind.Local),
    };

    private readonly HttpClient _http;
    private readonly ILogger<LocalModelProbe> _logger;

    public LocalModelProbe(HttpClient http, ILogger<LocalModelProbe> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Probe each known local server and return configured providers with a discovered model.
    /// Empty list means no local server is running (the UI then prompts for a cloud key).
    /// </summary>
    public async Task<IReadOnlyList<ChatProviderConfig>> DetectAsync(CancellationToken ct = default)
    {
        var found = new List<ChatProviderConfig>();

        foreach ((string name, string baseUrl, ChatProviderKind kind) in Candidates)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            try
            {
                // The OpenAI-compatible /models endpoint lists available models.
                string json = await _http.GetStringAsync(baseUrl + "/models", cts.Token);
                string? model = PickFirstModel(json);
                if (string.IsNullOrEmpty(model))
                {
                    _logger.LogDebug("{Name} responded but had no models.", name);
                    continue;
                }
                found.Add(new ChatProviderConfig
                {
                    Kind = kind,
                    Name = name,
                    BaseUrl = baseUrl,
                    Model = model,
                });
                _logger.LogInformation("Detected local model server: {Name} ({Model}).", name, model);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogDebug("Local probe for {Name} failed: {Message}", name, ex.Message);
            }
        }
        return found;
    }

    private static string? PickFirstModel(string modelsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(modelsJson);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            foreach (var m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    return id.GetString();
            }
        }
        catch { /* malformed */ }
        return null;
    }
}
