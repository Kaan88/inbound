namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// EXT.R12: the Host starts the window exe with <c>--show-job &lt;id&gt;</c> when the extension's "Show in app" asks
/// for a download. When a main window is already running, this launch writes the id into one small file beside the
/// queued torrents (<see cref="TorrentOpenRequests.QueueDirectory"/>) and signals it to show itself; that window takes
/// the id when it shows. Only the newest request counts, and one older than <see cref="MaxAge"/> is dropped.
/// </summary>
internal static class ShowJobRequests
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    /// <summary>When the window can show the asked-for download, which is always on this PC.</summary>
    public enum When
    {
        /// <summary>This PC's list is in: at once.</summary>
        Now,

        /// <summary>This PC's list is not in yet: once it is.</summary>
        AfterList,

        /// <summary>The window shows another server: it switches to This PC and shows it once that list is in.</summary>
        AfterSwitch,
    }

    public static When ShowWhen(bool showingAnotherServer, bool listIn) =>
        showingAnotherServer ? When.AfterSwitch : listIn ? When.Now : When.AfterList;

    /// <summary>A list that came in brings the waiting download only when it is this PC's.</summary>
    public static bool ListBringsIt(bool showingAnotherServer) => !showingAnotherServer;

    private const string FileName = "show-job.request";

    public static void Write(string directory, Guid jobId, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, $"{now.UtcTicks:D20} {jobId:D}");
        File.Move(temp, Path.Combine(directory, FileName), overwrite: true);
    }

    /// <summary>The waiting request's download, deleting the request; null when there is none, or it is stale or unreadable.</summary>
    public static Guid? Take(string directory, DateTimeOffset now)
    {
        var path = Path.Combine(directory, FileName);
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var parts = text.Trim().Split(' ', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParse(parts[1], out var id) || id == Guid.Empty)
            return null;
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return null;
        var age = now - new DateTimeOffset(ticks, TimeSpan.Zero);
        return age <= MaxAge && age >= -MaxAge ? id : null;
    }
}
