using System.Text.Json;

namespace DownloadManager.Desktop.WinUI;

/// <summary>T12: a list's column widths and the order it was sorted in.</summary>
internal sealed class ListLayout
{
    public Dictionary<string, double> Widths { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Null keeps the list's own order.</summary>
    public string? SortKey { get; set; }

    public bool SortAscending { get; set; }
}

/// <summary>
/// T12: where the lists of windows other than the main one keep their column widths and sort, by list
/// ("progress.files", "progress.peers", …). Not ui-state.json: Progress Details runs in a process of its own and the
/// main window writes that file whole. Like <see cref="WindowPlacement"/>, each save reads the file again and changes
/// only its own list, so several windows open at once never undo one another's widths. A convenience: nothing here
/// throws, and a file that can't be read is the lists' usual layout.
/// </summary>
internal sealed class ListLayoutStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;

    public ListLayoutStore(string path) => _path = path;

    /// <summary>The app's file, beside window-placement.json.</summary>
    public static ListLayoutStore Default { get; } = new(Path.Combine(
        Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA") is { Length: > 0 } isolated ? isolated : DownloadManager.Contracts.AppFolders.DataRoot(),
        "list-layout.json"));

    public ListLayout Load(string list) => ReadAll().TryGetValue(list, out var saved) && saved is not null ? saved : new ListLayout();

    public bool Save(string list, ListLayout layout)
    {
        try
        {
            var all = ReadAll();
            all[list] = layout;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, Options));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private Dictionary<string, ListLayout> ReadAll()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, ListLayout>>(File.ReadAllText(_path), Options)
                       ?? new Dictionary<string, ListLayout>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }

        return new Dictionary<string, ListLayout>(StringComparer.Ordinal);
    }
}
