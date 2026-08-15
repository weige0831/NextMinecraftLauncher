using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NML.App.Services;
using NML.Core.Instances;
using NML.Core.Multiplayer;

namespace NML.App.ViewModels.Pages;

/// <summary>
/// Multiplayer page: a saved-server list with live Server-List-Ping status (MOTD, player
/// count, latency, favicon), add/remove/reorder operations, and one-click connect that
/// launches the active instance with the <c>--server</c> argument.
/// <para>
/// Mirrors the multiplayer screen found in HMCL/PCL: the launcher does not embed the game,
/// but lets the user maintain a roster of favorite servers and see at a glance which are
/// up, how full they are, and how fast they respond.
/// </para>
/// </summary>
public partial class MultiplayerPageViewModel : PageViewModelBase
{
    public override string TitleKey => "nav.multiplayer";
    public override string Icon => "◉";

    private readonly ServerListStore _store;
    private readonly ServerPinger _pinger;
    private readonly InstanceStore _instances;
    private readonly ILogger<MultiplayerPageViewModel> _logger;

    /// <summary>Saved server entries, ordered as the user arranged them.</summary>
    public ObservableCollection<ServerEntry> Servers { get; } = new();

    [ObservableProperty] private ServerEntry? _selectedServer;
    [ObservableProperty] private string _newServerName = string.Empty;
    [ObservableProperty] private string _newServerAddress = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>The QR-shareable URI for the selected server (shown when the Share QR button is clicked).</summary>
    [ObservableProperty] private string _qrCodeUri = string.Empty;
    /// <summary>True when a QR URI is visible (drives the QR text-block visibility).</summary>
    [ObservableProperty] private bool _showQrCode;
    /// <summary>The rendered QR-code bitmap (bound to an Image), or null when none generated.</summary>
    private Avalonia.Media.Imaging.Bitmap? _qrBitmap;
    public Avalonia.Media.Imaging.Bitmap? QrBitmap
    {
        get => _qrBitmap;
        set => SetProperty(ref _qrBitmap, value);
    }

    /// <summary>True while a ping sweep of all servers is running.</summary>
    [ObservableProperty] private bool _isPingingAll;

    /// <summary>
    /// Wrap each saved entry with a live, UI-bindable status row so ping updates can flow
    /// through INotifyPropertyChanged without replacing the whole collection.
    /// </summary>
    public ObservableCollection<ServerRow> Rows { get; } = new();

    public MultiplayerPageViewModel(
        ServerListStore store,
        ServerPinger pinger,
        InstanceStore instances,
        ILogger<MultiplayerPageViewModel> logger)
    {
        _store = store;
        _pinger = pinger;
        _instances = instances;
        _logger = logger;
        EnsureLanguageSubscribed();
    }

    /// <summary>True when an address has been typed and we're not mid-operation.</summary>
    public bool CanAdd => !IsBusy && !string.IsNullOrWhiteSpace(NewServerAddress);

    /// <summary>True when a row is selected (drives remove / move-up / move-down / connect).</summary>
    public bool HasSelection => SelectedRow is not null;

    /// <summary>The currently-selected status row (mirrors <see cref="SelectedServer"/>).</summary>
    [ObservableProperty] private ServerRow? _selectedRow;

    partial void OnSelectedRowChanged(ServerRow? value)
    {
        SelectedServer = value?.Entry;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanConnect));
    }

    /// <summary>True when a row is selected AND an instance exists to launch.</summary>
    public bool CanConnect => SelectedRow is not null && _instances.LoadAll().Count > 0;

    /// <summary>Reload the saved list and refresh the bound rows on navigation.</summary>
    public override Task OnNavigatedToAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    /// <summary>Rebuild the <see cref="Rows"/> collection from the persisted store.</summary>
    private void Reload()
    {
        Servers.Clear();
        Rows.Clear();
        foreach (var s in _store.LoadAll())
        {
            Servers.Add(s);
            Rows.Add(new ServerRow { Entry = s });
        }
        OnPropertyChanged(nameof(CanConnect));
    }

    /// <summary>
    /// Parse "<c>host</c>" or "<c>host:port</c>" into a normalized (host, port) pair, defaulting
    /// the port to 25565 when omitted.
    /// </summary>
    private static (string host, int port) ParseAddress(string raw)
    {
        raw = raw.Trim();
        // IPv6 in brackets: [::1]:25565
        if (raw.StartsWith('['))
        {
            int close = raw.IndexOf(']');
            if (close > 0)
            {
                string host = raw[1..close];
                int port = 25565;
                if (close + 1 < raw.Length && raw[close + 1] == ':')
                    int.TryParse(raw[(close + 2)..], out port);
                return (host, port);
            }
        }
        int colon = raw.LastIndexOf(':');
        if (colon > 0 && int.TryParse(raw[(colon + 1)..], out int p) && p > 0 && p < 65536)
            return (raw[..colon], p);
        return (raw, 25565);
    }

    /// <summary>Add a new server from the address/name fields, then ping it once.</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(NewServerAddress)) return;

        // Accept either a raw host[:port] OR a shared mc://connect?... URI (from a QR scan / paste).
        // The QR path closes the loop with ShareQrCode: a friend scans/pastes the URI and lands here.
        string host; int port; string name;
        var parsed = NML.Core.Multiplayer.ServerQrCodeUri.Parse(NewServerAddress.Trim());
        if (parsed is { } uri)
        {
            (name, host, port) = (uri.Name, uri.Host, uri.Port);
            // If the user also typed a name, prefer it over the URI's name.
            if (!string.IsNullOrWhiteSpace(NewServerName)) name = NewServerName.Trim();
        }
        else
        {
            (host, port) = ParseAddress(NewServerAddress);
            name = string.IsNullOrWhiteSpace(NewServerName) ? NewServerAddress.Trim() : NewServerName.Trim();
        }

        var entry = new ServerEntry { Name = name, Host = host, Port = port };
        _store.Add(entry);
        NewServerName = string.Empty;
        NewServerAddress = string.Empty;
        Reload();

        // Ping the freshly added entry so the user gets immediate feedback.
        await PingRowAsync(Rows.Last());
    }

    /// <summary>Remove the selected server from the store and the list.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedRow is null) return;
        _store.Remove(SelectedRow.Entry.Name);
        Reload();
        Status = string.Empty;
    }

    /// <summary>Move the selected server up in the list.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp()
    {
        if (SelectedRow is null) return;
        int idx = Rows.IndexOf(SelectedRow);
        if (idx <= 0) return;
        _store.Move(SelectedRow.Entry.Name, idx - 1);
        Reload();
        // Re-select the same logical entry at its new position.
        if (idx - 1 < Rows.Count) SelectedRow = Rows[idx - 1];
    }

    /// <summary>Move the selected server down in the list.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown()
    {
        if (SelectedRow is null) return;
        int idx = Rows.IndexOf(SelectedRow);
        if (idx < 0 || idx >= Rows.Count - 1) return;
        _store.Move(SelectedRow.Entry.Name, idx + 1);
        Reload();
        if (idx + 1 < Rows.Count) SelectedRow = Rows[idx + 1];
    }

    /// <summary>Ping only the selected server.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PingSelectedAsync()
    {
        if (SelectedRow is null) return;
        await PingRowAsync(SelectedRow);
    }

    /// <summary>Ping every saved server concurrently and refresh their status rows.</summary>
    [RelayCommand]
    private async Task PingAllAsync()
    {
        if (Rows.Count == 0) return;
        IsPingingAll = true;
        Status = "server.pinging_all";
        try
        {
            await Task.WhenAll(Rows.Select(r => PingRowAsync(r, swallowExceptions: true)));
            Status = "server.ping_all_done";
        }
        finally
        {
            IsPingingAll = false;
        }
    }

    /// <summary>
    /// Ping a single row and update its live status. By default a failure is surfaced as a
    /// status message; when <paramref name="swallowExceptions"/> is true (ping-all sweep)
    /// the failure is recorded on the row but not broadcast.
    /// </summary>
    private async Task PingRowAsync(ServerRow row, bool swallowExceptions = false)
    {
        row.StatusText = "server.pinging";
        row.IsOnline = false;
        row.IsPinging = true;
        try
        {
            var snap = await _pinger.PingAsync(row.Entry.Host, row.Entry.Port).ConfigureAwait(false);
            row.ApplySnapshot(snap);
            row.StatusText = "server.online";
        }
        catch (Exception ex)
        {
            row.IsOnline = false;
            row.StatusText = "server.offline";
            _logger.LogWarning(ex, "Ping failed for {Host}:{Port}", row.Entry.Host, row.Entry.Port);
            if (!swallowExceptions) Status = $"server.ping_failed: {ex.Message}";
        }
        finally
        {
            row.IsPinging = false;
        }
    }

    /// <summary>
    /// Launch the active instance and connect directly to the selected server by appending
    /// <c>--server host --port port</c> to the game args (vanilla Minecraft honors these).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        if (SelectedRow is null) return;
        var inst = _instances.LoadAll().FirstOrDefault();
        if (inst is null) { Status = "server.no_instance"; return; }
        // The ProcessLauncher consumes CustomGameArgs verbatim after the vanilla args.
        inst.CustomGameArgs = $"--server {SelectedRow.Entry.Host} --port {SelectedRow.Entry.Port}";
        _instances.SaveAll(_instances.LoadAll()); // persist is optional; the launch path reads the in-memory copy
        Status = "server.connect_queued";
    }

    /// <summary>Path to a servers-export zip to import (bound to a textbox).</summary>
    [ObservableProperty] private string _importServersPath = string.Empty;

    /// <summary>Generate and display the QR-shareable URI for the selected server (and copy it to clipboard).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShareQrCode()
    {
        if (SelectedRow is null) return;
        QrCodeUri = ServerQrCodeUri.Build(SelectedRow.Entry.Name, SelectedRow.Entry.Host, SelectedRow.Entry.Port);
        ShowQrCode = true;

        // Generate a scannable QR-code bitmap from the URI via QRCoder (cross-platform, no System.Drawing).
        try
        {
            using var qrGen = new QRCoder.QRCodeGenerator();
            var qrData = qrGen.CreateQrCode(QrCodeUri, QRCoder.QRCodeGenerator.ECCLevel.M);
            var pngQr = new QRCoder.PngByteQRCode(qrData);
            byte[] pngBytes = pngQr.GetGraphic(8);
            using var ms = new System.IO.MemoryStream(pngBytes);
            QrBitmap = new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "QR image generation failed; falling back to text-only.");
            QrBitmap = null;
        }

        // Also copy to clipboard so the user can paste into any QR generator.
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { Clipboard: var cb } && cb is not null)
                cb.SetTextAsync(QrCodeUri).GetAwaiter().GetResult();
        }
        catch { /* non-fatal */ }
        Status = "server.qr.generated";
    }

    /// <summary>Export the saved server list to a portable .zip on the desktop.</summary>
    [RelayCommand]
    private void ExportServers()
    {
        try
        {
            string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            string zipPath = System.IO.Path.Combine(desktop, "nml-servers.zip");
            _store.ExportToZip(zipPath);
            Status = $"server.exported,{zipPath}";
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }

    /// <summary>Import servers from a portable .zip, merging into the current list.</summary>
    /// <summary>Open an OS file-picker to choose a servers .zip and populate ImportServersPath.</summary>
    [RelayCommand]
    private async Task BrowseServersAsync()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is null) return;
            var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import server list",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Server list zip") { Patterns = new[] { "*.zip" } },
                },
            });
            if (files.Count > 0) ImportServersPath = files[0].Path.LocalPath;
        }
        catch { /* non-fatal */ }
    }

    [RelayCommand]
    private void ImportServers()
    {
        string zipPath = ImportServersPath?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(zipPath)) { Status = "server.import.needed"; return; }
        try
        {
            int count = _store.ImportFromZip(zipPath);
            Status = $"server.imported,{count}";
            Reload();
        }
        catch (Exception ex) { Status = $"common.error,{ex.Message}"; }
    }

    // Re-evaluate CanExecute on the relevant observables.
    partial void OnNewServerAddressChanged(string value) => AddCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => AddCommand.NotifyCanExecuteChanged();
}/// <summary>
/// A live, INotifyPropertyChanged wrapper around a <see cref="ServerEntry"/> so the UI can
/// bind to per-row ping status (MOTD, player count, latency, online state) without the page
/// VM having to mutate the persisted model.
/// </summary>
public partial class ServerRow : ObservableObject
{
    /// <summary>The persisted entry this row mirrors.</summary>
    public ServerEntry Entry { get; set; } = new();

    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isPinging;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _motd = string.Empty;
    [ObservableProperty] private int _onlinePlayers;
    [ObservableProperty] private int _maxPlayers;
    [ObservableProperty] private int _latencyMs;
    [ObservableProperty] private string _versionName = string.Empty;
    [ObservableProperty] private string? _faviconUrl;

    /// <summary>"12/20"-style player count string for display.</summary>
    public string PlayerCount => $"{OnlinePlayers}/{MaxPlayers}";

    /// <summary>Latency badge text (e.g. "42 ms").</summary>
    public string LatencyText => LatencyMs > 0 ? $"{LatencyMs} ms" : string.Empty;

    /// <summary>Copy a ping snapshot into the bindable display fields.</summary>
    public void ApplySnapshot(ServerPingSnapshot snap)
    {
        IsOnline = true;
        Motd = string.IsNullOrEmpty(snap.MotdLine2)
            ? snap.MotdLine1
            : $"{snap.MotdLine1}\n{snap.MotdLine2}";
        OnlinePlayers = snap.OnlinePlayers;
        MaxPlayers = snap.MaxPlayers;
        LatencyMs = snap.LatencyMs;
        VersionName = snap.VersionName;
        FaviconUrl = snap.FaviconDataUrl;
        // Entry.LastPing is cached for any future offline display.
        Entry.LastPing = snap;
        // Re-raise derived properties.
        OnPropertyChanged(nameof(PlayerCount));
        OnPropertyChanged(nameof(LatencyText));
    }
}
