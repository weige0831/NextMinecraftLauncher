using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NML.AICore;
using NML.App.Services;

namespace NML.App.ViewModels.Pages;

/// <summary>
/// AI assistant chat page. Streams responses token-by-token from the active provider.
/// Shows a friendly "no provider configured" hint when AI isn't set up yet.
/// </summary>
public partial class AssistantPageViewModel : PageViewModelBase
{
    public override string TitleKey => "nav.assistant";
    public override string Icon => "✦";

    private readonly ChatClientFactory _factory;
    private readonly SettingsStore _settings;
    private readonly ILogger<AssistantPageViewModel> _logger;

    public ObservableCollection<ChatTurn> Conversation { get; } = new();

    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    // --- Command-block generator fields ---
    [ObservableProperty] private string _cmdTarget = "@p";
    [ObservableProperty] private string _cmdType = "give";
    [ObservableProperty] private string _cmdItemId = "diamond_sword";
    [ObservableProperty] private int _cmdCount = 1;
    [ObservableProperty] private double _cmdX = 0, _cmdY = 64, _cmdZ = 0;
    [ObservableProperty] private string _cmdEffectId = "speed";
    [ObservableProperty] private int _cmdDuration = 30;
    [ObservableProperty] private int _cmdAmplifier = 0;
    [ObservableProperty] private string _cmdGamemode = "creative";
    [ObservableProperty] private string _cmdOutput = string.Empty;

    public AssistantPageViewModel(
        ChatClientFactory factory,
        SettingsStore settings,
        ILogger<AssistantPageViewModel> logger)
    {
        _factory = factory;
        _settings = settings;
        _logger = logger;
        EnsureLanguageSubscribed();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Input)) return;

        LauncherSettings s = _settings.Load();
        ChatProviderConfig? cfg = s.Providers.FirstOrDefault(p => p.Name == s.ActiveProviderName);
        if (cfg is null) { Status = "assistant.no_provider"; return; }

        string userText = Input.Trim();
        Input = string.Empty;
        var userTurn = new ChatTurn { Role = "assistant.user", Content = userText };
        Conversation.Add(userTurn);

        var aiTurn = new ChatTurn { Role = "assistant.ai", Content = string.Empty };
        Conversation.Add(aiTurn);
        IsBusy = true;
        Status = "assistant.thinking";

        try
        {
            IChatClient client = _factory.Create(cfg);
            var messages = Conversation
                .Where(t => !string.IsNullOrEmpty(t.Content))
                .Select(t => new ChatMessage
                {
                    Role = t.Role == "assistant.user" ? ChatRole.User : ChatRole.Assistant,
                    Content = t.Content,
                }).ToList();

            await foreach (string chunk in client.StreamAsync(messages))
                aiTurn.Content += chunk;
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            aiTurn.Content = $"common.error: {ex.Message}";
            _logger.LogError(ex, "Chat failed.");
            Status = $"common.error,{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Generate a Minecraft command from the structured fields.</summary>
    [RelayCommand]
    private void GenerateCommand()
    {
        CmdOutput = CmdType switch
        {
            "give"     => NML.Core.Game.MinecraftCommandBuilder.Give(CmdTarget, CmdItemId, CmdCount),
            "tp"       => NML.Core.Game.MinecraftCommandBuilder.Teleport(CmdTarget, CmdX, CmdY, CmdZ),
            "effect"   => NML.Core.Game.MinecraftCommandBuilder.EffectGive(CmdTarget, CmdEffectId, CmdDuration, CmdAmplifier),
            "gamemode" => NML.Core.Game.MinecraftCommandBuilder.Gamemode(CmdTarget, CmdGamemode),
            "time"     => NML.Core.Game.MinecraftCommandBuilder.TimeSet(CmdGamemode == "day" ? "day" : "night"),
            "weather"  => NML.Core.Game.MinecraftCommandBuilder.Weather(CmdEffectId),
            _ => string.Empty,
        };
    }

    /// <summary>Copy the generated command to the clipboard.</summary>
    [RelayCommand]
    private void CopyCommand()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { Clipboard: var cb } && cb is not null)
                cb.SetTextAsync(CmdOutput).GetAwaiter().GetResult();
            Status = "cmd.copied";
        }
        catch { /* non-fatal */ }
    }
}

/// <summary>One turn in the chat conversation (user or assistant).</summary>
public sealed class ChatTurn : ObservableObject
{
    /// <summary>Localization key for the speaker label (<c>assistant.user</c> or <c>assistant.ai</c>).</summary>
    public string Role { get; init; } = string.Empty;

    private string _content = string.Empty;
    /// <summary>Content text — observable so streaming updates render live.</summary>
    public string Content
    {
        get => _content;
        set => SetProperty(ref _content, value);
    }
}
