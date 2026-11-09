using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R32: where a provider panel was, so the same place shows after it moves between the main window's full view
/// (A), its drawer (C) and a window of its own. Ids, not rows: the rows may be read again in between.
/// </summary>
/// <param name="Kind">The open tab.</param>
/// <param name="SelectedItemId">The chosen item, if any.</param>
/// <param name="TopItemId">The item at the top of the list, which is how far it was scrolled.</param>
/// <param name="Destination">The folder typed or picked for new downloads (not one a job on the list has).</param>
internal sealed record ProviderViewState(string? Kind, string? SelectedItemId, string? TopItemId, string? Destination);

/// <summary>How a panel takes up a <see cref="ProviderViewState"/>. Nothing here touches the UI.</summary>
internal static class ProviderViewMemory
{
    /// <summary>The tab to open: the one it had, while the account still has that kind; otherwise the first.</summary>
    public static string? Tab(ProviderViewState? state, IReadOnlyList<string> kinds)
        => state?.Kind is { } kind && kinds.Contains(kind, StringComparer.Ordinal) ? kind : kinds.FirstOrDefault();

    /// <summary>The item to choose again, when it is still in the list. One that has gone leaves nothing chosen.</summary>
    public static string? Selected(ProviderViewState? state, IReadOnlyCollection<string> rowIds)
        => state?.SelectedItemId is { } id && rowIds.Contains(id) ? id : null;

    /// <summary>
    /// The item to scroll to the top. A row is a different height in each view, so the place is a row, not a distance.
    /// When the top row has gone, the chosen one; when both have, the list stays at its start.
    /// </summary>
    public static string? Top(ProviderViewState? state, IReadOnlyCollection<string> rowIds)
    {
        if (state?.TopItemId is { } top && rowIds.Contains(top)) return top;
        return Selected(state, rowIds);
    }

    /// <summary>The folder for new downloads: the one it had, or else the last used, or else the default.</summary>
    public static string Destination(ProviderViewState? state, string? lastUsed, string? fallback)
        => !string.IsNullOrWhiteSpace(state?.Destination) ? state!.Destination!
            : !string.IsNullOrWhiteSpace(lastUsed) ? lastUsed!
            : fallback ?? string.Empty;
}

/// <summary>The tag a catalog item gets once it is on the download list ("On list · 31%").</summary>
internal static class ProviderOnList
{
    /// <summary>
    /// Null while the item has no job, or only a failed or removed one: then it isn't on the list. A job still waiting on
    /// the provider is on the list as a remote row, so it is "On list" with no figure. Local files give the share of their
    /// bytes received, never 100% before every file is complete, and "done" when they are.
    /// </summary>
    public static string? Tag(RemoteJobDto? job, IReadOnlyCollection<JobDto> localFiles)
    {
        if (job is null || job.State is "failed" or "removed") return null;
        if (job.State != "localStarted" || localFiles.Count == 0) return "On list";
        if (localFiles.All(file => file.State == "completed")) return "On list · done";
        var sized = localFiles.Where(file => file.TotalBytes is > 0).ToList();
        if (sized.Count == 0) return "On list";
        var total = sized.Sum(file => file.TotalBytes!.Value);
        var received = sized.Sum(file => file.State == "completed" ? file.TotalBytes!.Value : Math.Min(file.ReceivedBytes, file.TotalBytes!.Value));
        var percent = (int)Math.Floor(received * 100.0 / total);
        return $"On list · {Math.Clamp(percent, 0, 99)}%";
    }
}

/// <summary>Whether the drawer can sit beside the list or has to cover it. Nothing here touches the UI.</summary>
internal static class ProviderDrawerLayout
{
    public const double MinimumWidth = 340;
    public const double MaximumWidth = 720;
    public const double DefaultWidth = 420;

    /// <summary>The narrowest the download list may get beside a pinned drawer before the drawer covers it instead.</summary>
    public const double MinimumListWidth = 520;

    /// <summary>A saved or dragged width, kept between the drawer's limits and never wider than the room there is.</summary>
    public static double Width(double? wanted, double available)
    {
        var width = wanted is double value && value > 0 && !double.IsInfinity(value) ? value : DefaultWidth;
        var most = Math.Max(MinimumWidth, Math.Min(MaximumWidth, available - 120));
        return Math.Clamp(width, MinimumWidth, most);
    }

    /// <summary>
    /// Pinned docks it beside the list, while the list keeps <see cref="MinimumListWidth"/>. On a narrower window a pinned
    /// drawer covers the list as an unpinned one does, and docks again when there is room. Unpinned always covers it.
    /// </summary>
    public static bool Inline(bool pinned, double available, double drawerWidth)
        => pinned && available - drawerWidth >= MinimumListWidth;
}

/// <summary>The notice in the download list after a Download from a provider's catalog.</summary>
internal static class ProviderNotice
{
    /// <param name="files">How many files were sent, or null when all of them were and the count isn't known yet.</param>
    public static string Added(int? files, string item) => files switch
    {
        null => $"Added all files from {item}",
        1 => $"Added 1 file from {item}",
        _ => $"Added {files} files from {item}",
    };
}

/// <summary>UI.R32 option (owner, 2026-11-09): which view My provider opens in first. The other is one click on from it.</summary>
internal static class ProviderOpening
{
    public const string Full = "full";
    public const string Drawer = "drawer";

    /// <summary>The choices in the order Options lists them; the first is the default.</summary>
    public static readonly string[] Choices = [Full, Drawer];

    /// <summary>The saved choice, or the full view (A) for anything else, including nothing saved.</summary>
    public static string Normalize(string? saved) => saved == Drawer ? Drawer : Full;

    /// <summary>Whether a way into My provider (the sidebar, Tasks, Show in My provider) opens the drawer first.</summary>
    public static bool OpensDrawer(string? saved) => Normalize(saved) == Drawer;
}
