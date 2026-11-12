using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Audit H1: Options stays open while other things change the settings (Stop Queue, the Scheduler, the toolbar limit
/// menu, the CLI, a web page). Sending the copy it took when it opened would undo all of that. Options instead reads
/// the Host's latest settings when it saves and puts onto them only the fields the user changed on its pages: a field
/// still as it was when Options opened keeps the Host's latest value, and fields Options has no page for (queues,
/// schedules, recent folders and the rest) are never taken from its copy.
/// </summary>
public static class OptionsSettingsRebase
{
    /// <summary>
    /// Puts onto <paramref name="latest"/> (changed in place and returned) each Options field where
    /// <paramref name="edited"/> differs from <paramref name="atOpen"/>. Lists with an identity (site logins, custom
    /// categories, favourites) and the two maps are merged per entry, so an entry added or changed elsewhere survives
    /// an edit of another one.
    /// </summary>
    public static SettingsDto Rebase(SettingsDto atOpen, SettingsDto edited, SettingsDto latest)
    {
        if (Changed(atOpen.DefaultDownloadDirectory, edited.DefaultDownloadDirectory)) latest.DefaultDownloadDirectory = edited.DefaultDownloadDirectory;
        if (Changed(atOpen.MaxConcurrentDownloads, edited.MaxConcurrentDownloads)) latest.MaxConcurrentDownloads = edited.MaxConcurrentDownloads;
        if (Changed(atOpen.DefaultConnections, edited.DefaultConnections)) latest.DefaultConnections = edited.DefaultConnections;
        if (Changed(atOpen.GlobalSpeedLimitBytesPerSecond, edited.GlobalSpeedLimitBytesPerSecond)) latest.GlobalSpeedLimitBytesPerSecond = edited.GlobalSpeedLimitBytesPerSecond;
        if (Changed(atOpen.LaunchAtStartup, edited.LaunchAtStartup)) latest.LaunchAtStartup = edited.LaunchAtStartup;
        if (Changed(atOpen.CloseWindowCompletely, edited.CloseWindowCompletely)) latest.CloseWindowCompletely = edited.CloseWindowCompletely;
        if (Changed(atOpen.Theme, edited.Theme)) latest.Theme = edited.Theme;
        if (Changed(atOpen.UseCategoryFolders, edited.UseCategoryFolders)) latest.UseCategoryFolders = edited.UseCategoryFolders;
        // T14: torrent category folders and the torrent folder (Save to page).
        if (Changed(atOpen.UseTorrentCategoryFolders, edited.UseTorrentCategoryFolders)) latest.UseTorrentCategoryFolders = edited.UseTorrentCategoryFolders;
        if (Changed(atOpen.TorrentCategoryFoldersRoot, edited.TorrentCategoryFoldersRoot)) latest.TorrentCategoryFoldersRoot = edited.TorrentCategoryFoldersRoot;
        if (Changed(atOpen.ClipboardMonitoring, edited.ClipboardMonitoring)) latest.ClipboardMonitoring = edited.ClipboardMonitoring;
        if (Changed(atOpen.BrowserIntegrationEnabled, edited.BrowserIntegrationEnabled)) latest.BrowserIntegrationEnabled = edited.BrowserIntegrationEnabled;
        if (Changed(atOpen.NotifyOnCompletion, edited.NotifyOnCompletion)) latest.NotifyOnCompletion = edited.NotifyOnCompletion;
        if (Changed(atOpen.PlayCompletionSound, edited.PlayCompletionSound)) latest.PlayCompletionSound = edited.PlayCompletionSound;
        if (Changed(atOpen.ShowCompletionDialog, edited.ShowCompletionDialog)) latest.ShowCompletionDialog = edited.ShowCompletionDialog;
        if (Changed(atOpen.AutoResumeUnfinishedOnStartup, edited.AutoResumeUnfinishedOnStartup)) latest.AutoResumeUnfinishedOnStartup = edited.AutoResumeUnfinishedOnStartup;
        if (Changed(atOpen.PostDownloadCommand, edited.PostDownloadCommand)) latest.PostDownloadCommand = edited.PostDownloadCommand;
        if (Changed(atOpen.AutoCheckUpdates, edited.AutoCheckUpdates)) latest.AutoCheckUpdates = edited.AutoCheckUpdates;
        if (Changed(atOpen.UpdateCheckIntervalHours, edited.UpdateCheckIntervalHours)) latest.UpdateCheckIntervalHours = edited.UpdateCheckIntervalHours;
        if (Changed(atOpen.ConnectionProfiles, edited.ConnectionProfiles)) latest.ConnectionProfiles = edited.ConnectionProfiles;
        // The Torrents page (TorrentOptionsPage.ApplyTo).
        if (Changed(atOpen.TorrentsEnabled, edited.TorrentsEnabled)) latest.TorrentsEnabled = edited.TorrentsEnabled;
        if (Changed(atOpen.TorrentRoute, edited.TorrentRoute)) latest.TorrentRoute = edited.TorrentRoute;
        if (Changed(atOpen.TorrentChooseFiles, edited.TorrentChooseFiles)) latest.TorrentChooseFiles = edited.TorrentChooseFiles;
        if (Changed(atOpen.TorrentHandler, edited.TorrentHandler)) latest.TorrentHandler = edited.TorrentHandler;
        if (Changed(atOpen.TorrentSeedAfterDownload, edited.TorrentSeedAfterDownload)) latest.TorrentSeedAfterDownload = edited.TorrentSeedAfterDownload;
        if (Changed(atOpen.TorrentSeedRatio, edited.TorrentSeedRatio)) latest.TorrentSeedRatio = edited.TorrentSeedRatio;
        if (Changed(atOpen.TorrentSeedHours, edited.TorrentSeedHours)) latest.TorrentSeedHours = edited.TorrentSeedHours;
        if (Changed(atOpen.TorrentUploadLimit, edited.TorrentUploadLimit)) latest.TorrentUploadLimit = edited.TorrentUploadLimit;
        if (Changed(atOpen.TorrentListenPort, edited.TorrentListenPort)) latest.TorrentListenPort = edited.TorrentListenPort;
        if (Changed(atOpen.TorrentUpnp, edited.TorrentUpnp)) latest.TorrentUpnp = edited.TorrentUpnp;
        if (Changed(atOpen.TorrentDht, edited.TorrentDht)) latest.TorrentDht = edited.TorrentDht;
        if (Changed(atOpen.TorrentPex, edited.TorrentPex)) latest.TorrentPex = edited.TorrentPex;
        if (Changed(atOpen.TorrentLsd, edited.TorrentLsd)) latest.TorrentLsd = edited.TorrentLsd;
        if (Changed(atOpen.TorrentEncryption, edited.TorrentEncryption)) latest.TorrentEncryption = edited.TorrentEncryption;
        if (Changed(atOpen.TorrentSeedsAtOnce, edited.TorrentSeedsAtOnce)) latest.TorrentSeedsAtOnce = edited.TorrentSeedsAtOnce;
        if (Changed(atOpen.TorrentConnectionsPerTorrent, edited.TorrentConnectionsPerTorrent)) latest.TorrentConnectionsPerTorrent = edited.TorrentConnectionsPerTorrent;
        if (Changed(atOpen.TorrentMaxConnections, edited.TorrentMaxConnections)) latest.TorrentMaxConnections = edited.TorrentMaxConnections;
        if (Changed(atOpen.TorrentUseProxy, edited.TorrentUseProxy)) latest.TorrentUseProxy = edited.TorrentUseProxy;
        // A typed password or Forget is a change; an untouched proxy keeps the Host's (and its stored password).
        if (Changed(atOpen.Proxy, edited.Proxy)) latest.Proxy = edited.Proxy;

        latest.SiteLogins = MergeById(atOpen.SiteLogins, edited.SiteLogins, latest.SiteLogins, login => login.Id.ToString());
        latest.CustomCategories = MergeById(atOpen.CustomCategories, edited.CustomCategories, latest.CustomCategories, category => category.Id);
        latest.FavoriteFolders = MergeById(atOpen.FavoriteFolders, edited.FavoriteFolders, latest.FavoriteFolders,
            favorite => favorite.Path.ToUpperInvariant());
        latest.CategoryFolders = MergeMap(atOpen.CategoryFolders, edited.CategoryFolders, latest.CategoryFolders);
        latest.EventSoundFiles = MergeMap(atOpen.EventSoundFiles, edited.EventSoundFiles, latest.EventSoundFiles);
        latest.DeleteCategoryIds = edited.DeleteCategoryIds.ToList();
        return latest;
    }

    /// <summary>The Options speed limit box's largest value, in KB/s.</summary>
    public const long SpeedLimitMaxKb = 10_000_000;

    // ---- Review 5:F6b: Options › Connection's per-site limits, a row each (a site and its connections) ----

    public const string SiteLimitNeedsSite = "Enter a site for each per-site limit, or remove it.";

    public static string SiteLimitOutOfRange(string site) => $"The limit for {site} must be 1 to 32 connections.";

    /// <summary>
    /// The per-site limits the rows give, or the first row's problem. A row left wholly empty is skipped. The web UI's
    /// readProfiles (webui settingsRules.ts) by the same rule. <paramref name="rows"/>: the site as typed and the number
    /// box's value (NaN when empty).
    /// </summary>
    public static (List<ConnectionProfileDto> Profiles, string? Problem) SiteLimits(IEnumerable<(string Site, double Max)> rows)
    {
        var profiles = new List<ConnectionProfileDto>();
        foreach (var (typed, max) in rows)
        {
            var site = typed.Trim();
            if (site.Length == 0 && double.IsNaN(max)) continue;
            if (site.Length == 0) return ([], SiteLimitNeedsSite);
            if (double.IsNaN(max) || max != Math.Floor(max) || max is < 1 or > 32) return ([], SiteLimitOutOfRange(site));
            profiles.Add(new ConnectionProfileDto { Pattern = site, MaxConnections = (int)max });
        }
        return (profiles, null);
    }

    /// <summary>
    /// Audit H1: the opened settings as the Options form gives them back untouched, for the <c>atOpen</c> side of
    /// <see cref="Rebase"/>. Some fields are shown in a coarser form than they are stored: the speed limit in whole
    /// KB/s (the toolbar's MB/s choice or the CLI can store any byte count), and the proxy rebuilt from its mode, type
    /// and host:port (an address kept under "No proxy" or "system" comes back empty). Compared with the stored value,
    /// every OK would count those as changed and overwrite what another client set meanwhile. Changes
    /// <paramref name="settings"/> in place and returns it.
    /// </summary>
    public static SettingsDto AsShown(SettingsDto settings)
    {
        // MainWindow's Options: the box shows value / 1024, held to its 1-10,000,000 range, and saving sends kb * 1024.
        if (settings.GlobalSpeedLimitBytesPerSecond is { } limit && limit > 0)
            settings.GlobalSpeedLimitBytesPerSecond = Math.Clamp(limit / 1024, 1, SpeedLimitMaxKb) * 1024;
        var proxy = settings.Proxy;
        var mode = proxy.Mode is "system" or "manual" ? proxy.Mode : "none";
        var (protocol, hostPort) = ProxyAddress.Split(proxy.Address);
        settings.Proxy = new ProxyDto
        {
            Mode = mode,
            Address = mode == "manual" ? ProxyAddress.Compose(protocol, hostPort) : null,
            Username = string.IsNullOrWhiteSpace(proxy.Username) ? null : proxy.Username.Trim(),
            // The password box opens empty and Forget unchecked: the stored password is kept.
            Password = null,
            ClearPassword = false,
            HasPassword = proxy.HasPassword,
        };
        return settings;
    }

    private static bool Changed<T>(T before, T after)
        => !string.Equals(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after), StringComparison.Ordinal);

    /// <summary>
    /// The edited list in its order, where an entry the user left as it was takes the Host's latest version (and is
    /// dropped if it was removed elsewhere), then the entries added elsewhere since Options opened. An entry the user
    /// removed stays removed; one the user changed or added is sent as edited.
    /// </summary>
    internal static List<T> MergeById<T>(List<T> atOpen, List<T> edited, List<T> latest, Func<T, string> key)
    {
        var openByKey = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in atOpen) openByKey.TryAdd(key(item), item);
        var latestByKey = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in latest) latestByKey.TryAdd(key(item), item);
        var result = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in edited)
        {
            var id = key(item);
            if (!seen.Add(id)) continue;
            if (openByKey.TryGetValue(id, out var original) && !Changed(original, item))
            {
                if (latestByKey.TryGetValue(id, out var current)) result.Add(current);
            }
            else
            {
                result.Add(item);
            }
        }
        foreach (var item in latest)
        {
            var id = key(item);
            if (!openByKey.ContainsKey(id) && seen.Add(id)) result.Add(item);
        }
        return result;
    }

    /// <summary>Per key: a value the user set, changed or cleared is applied; every other key keeps the Host's latest.</summary>
    internal static Dictionary<string, string> MergeMap(
        Dictionary<string, string> atOpen, Dictionary<string, string> edited, Dictionary<string, string> latest)
    {
        var result = new Dictionary<string, string>(latest, StringComparer.OrdinalIgnoreCase);
        foreach (var key in atOpen.Keys.Concat(edited.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = atOpen.TryGetValue(key, out var openValue) ? openValue : null;
            var after = edited.TryGetValue(key, out var editedValue) ? editedValue : null;
            if (string.Equals(before, after, StringComparison.Ordinal)) continue;
            if (after is null) result.Remove(key);
            else result[key] = after;
        }
        return result;
    }
}
