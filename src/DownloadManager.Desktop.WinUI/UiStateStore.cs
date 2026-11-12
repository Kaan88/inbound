using System.Text.Json;

namespace DownloadManager.Desktop.WinUI;

internal sealed class UiState
{
    /// <summary>In device-independent pixels until the window is first closed, and in real pixels after that.</summary>
    public const int FirstWindowWidth = 1640;
    public const int FirstWindowHeight = 940;

    public int WindowWidth { get; set; } = FirstWindowWidth;
    public int WindowHeight { get; set; } = FirstWindowHeight;
    public string Category { get; set; } = "all";
    public string SortKey { get; set; } = "added";
    public bool SortAscending { get; set; }

    /// <summary>Widths the download list's columns were dragged to, by column. A column left out has its usual width.</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new();

    /// <summary>UI.R42: the download list's columns shown (true) or hidden (false) from its header's menu, in every view but the
    /// torrent views. A column left out shows as it does at first.</summary>
    public Dictionary<string, bool> ShownColumns { get; set; } = new();

    /// <summary>UI.R42: the same for the torrent views, which keep their own choice.</summary>
    public Dictionary<string, bool> TorrentViewColumns { get; set; } = new();

    /// <summary>The same for My provider's lists, which share one set of columns.</summary>
    public Dictionary<string, double> ProviderColumnWidths { get; set; } = new();

    /// <summary>UI.R42: My provider's columns shown or hidden from the header's menu.</summary>
    public Dictionary<string, bool> ProviderShownColumns { get; set; } = new();

    /// <summary>Null keeps the order the provider answers in.</summary>
    public string? ProviderSortKey { get; set; }
    public bool ProviderSortAscending { get; set; }

    /// <summary>UI.R32: My provider's drawer docks beside the list instead of covering it.</summary>
    public bool ProviderDrawerPinned { get; set; }

    /// <summary>UI.R32: the drawer's width as last dragged; null for the default.</summary>
    public double? ProviderDrawerWidth { get; set; }

    /// <summary>UI.R32: "full" (the default) or "drawer", the view My provider opens in first (Options > General).</summary>
    public string? ProviderOpensAs { get; set; }
    public bool ToolbarLarge { get; set; } = true;
    public bool HideCategories { get; set; }
    public bool ShowSpeedGraph { get; set; } = true;
    public bool SpeedGraphExpanded { get; set; } = true;
    /// <summary>T7: View > Torrent Info, the pane beside the list for the one selected torrent.</summary>
    public bool ShowTorrentInfo { get; set; } = true;
    /// <summary>View > Dark Mode from before the Theme setting was the source (UI.R23). Moved into the setting once,
    /// then cleared; null when it was never set or has moved.</summary>
    public bool? DarkMode { get; set; }

    /// <summary>The Theme setting last seen (System, Light or Dark): a start-up hint only, so a window opened before
    /// the Host's settings arrive does not flash the other theme. The setting is the source.</summary>
    public string? Theme { get; set; }
    public List<Guid> ExpandedPackages { get; set; } = new();

    /// <summary>UPD.N1: the installed version whose What's new was last shown or skipped; null before 0.7.2 kept it.</summary>
    public string? WhatsNewSeen { get; set; }

    /// <summary>UI.R41: the "get the extension" bar is done with: Not now was clicked, or an extension has connected.</summary>
    public bool ExtensionHintDone { get; set; }
}

internal static class UiStateStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static string[] Paths
    {
        get
        {
            var isolated = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA");
            if (!string.IsNullOrWhiteSpace(isolated)) return [Path.Combine(isolated, "ui-state.json")];
            return
            [
                Path.Combine(DownloadManager.Contracts.AppFolders.DataRoot(), "ui-state.json"),
                Path.Combine(AppContext.BaseDirectory, "ui-state.json"),
            ];
        }
    }

    private static bool? _existedAtStart;

    /// <summary>
    /// UPD.N1: whether ui-state.json was there when this process first read it, before anything here saved one. A window
    /// that has run before (closing it saves the layout) left one, so a start without it is a fresh install.
    /// </summary>
    public static bool ExistedAtStart => _existedAtStart ??= Paths.Any(File.Exists);

    public static UiState Load()
    {
        _ = ExistedAtStart;
        foreach (var path in Paths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(path), Options)
                       ?? new UiState();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // try the next location
            }
        }

        return new UiState();
    }

    /// <summary>
    /// Saves the layout; false when no location took it. Audit L1: it never throws, since a full disk or a locked file
    /// must not end the window from a click that only expanded a package.
    /// </summary>
    public static bool Save(UiState state)
    {
        _ = ExistedAtStart;
        Exception? last = null;
        foreach (var path in Paths)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                last = ex;
            }
        }

        if (last is not null) UiTrace.Write($"UI state save failed: {last.Message}");
        return false;
    }
}
