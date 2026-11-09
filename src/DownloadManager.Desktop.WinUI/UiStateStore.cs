using System.Text.Json;

namespace DownloadManager.Desktop.WinUI;

internal sealed class UiState
{
    /// <summary>In device-independent pixels until the window is first closed, and in real pixels after that.</summary>
    public const int FirstWindowWidth = 1500;
    public const int FirstWindowHeight = 860;

    public int WindowWidth { get; set; } = FirstWindowWidth;
    public int WindowHeight { get; set; } = FirstWindowHeight;
    public string Category { get; set; } = "all";
    public string SortKey { get; set; } = "added";
    public bool SortAscending { get; set; }

    /// <summary>Widths the download list's columns were dragged to, by column. A column left out has its usual width.</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new();

    /// <summary>The same for My provider's lists, which share one set of columns.</summary>
    public Dictionary<string, double> ProviderColumnWidths { get; set; } = new();

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
    /// <summary>View > Dark Mode from before the Theme setting was the source (UI.R23). Moved into the setting once,
    /// then cleared; null when it was never set or has moved.</summary>
    public bool? DarkMode { get; set; }

    /// <summary>The Theme setting last seen (System, Light or Dark): a start-up hint only, so a window opened before
    /// the Host's settings arrive does not flash the other theme. The setting is the source.</summary>
    public string? Theme { get; set; }
    public List<Guid> ExpandedPackages { get; set; } = new();
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

    public static UiState Load()
    {
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

    public static void Save(UiState state)
    {
        Exception? last = null;
        foreach (var path in Paths)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
            }
        }

        if (last is not null)
        {
            throw last;
        }
    }
}
