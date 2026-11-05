using System.Text.Json;

namespace DownloadManager.Desktop.WinUI;

internal sealed class UiState
{
    public int WindowWidth { get; set; } = 1180;
    public int WindowHeight { get; set; } = 760;
    public string Category { get; set; } = "all";
    public string SortKey { get; set; } = "added";
    public bool SortAscending { get; set; }
    public bool ToolbarLarge { get; set; } = true;
    public bool HideCategories { get; set; }
    public bool ShowSpeedGraph { get; set; } = true;
    public bool SpeedGraphExpanded { get; set; } = true;
    public bool? DarkMode { get; set; }
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
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager", "ui-state.json"),
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
