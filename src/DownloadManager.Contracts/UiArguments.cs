namespace DownloadManager.Contracts;

/// <summary>Command-line arguments a Host passes to the desktop window it starts.</summary>
public static class UiArguments
{
    /// <summary>
    /// EXT.R12: <c>--show-job &lt;id&gt;</c> opens the main window at that download, selected and briefly highlighted. A
    /// window already running is asked to do it.
    /// </summary>
    public const string ShowJob = "--show-job";

    /// <summary>The download <see cref="ShowJob"/> names, or null when there is none or it isn't a download's id.</summary>
    public static Guid? ShowJobId(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], ShowJob, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(args[i + 1], out var id) && id != Guid.Empty)
                return id;
        }

        return null;
    }
}
