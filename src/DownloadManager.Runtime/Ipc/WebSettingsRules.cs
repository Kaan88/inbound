using System.Reflection;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// Which settings a browser on another device (a <c>web</c> session, D8) may change on the desktop Host. An allow-list:
/// every <see cref="SettingsDto"/> field is in exactly one of <see cref="Allowed"/>, <see cref="PipeOnly"/> and
/// <see cref="KeptByHost"/>, and a test fails when a new field is in none, so each new setting is decided on.
/// <list type="bullet">
/// <item><see cref="Allowed"/>: downloads, connections, speed, queues and schedule times, categories' names and
/// file types, and where torrents go and how long they seed. Some carry a rule inside: a schedule's when-done actions and a category's folder stay as they are.</item>
/// <item><see cref="PipeOnly"/>: what reaches beyond the downloads (proxy, local network, clipboard, the browser
/// integration and its lists, start at sign-in, the window, sounds, theme, updates, programs) and every folder, because
/// the folders are the session's own roots and it must not widen them. A save must bring these back as they are, or
/// it is refused and nothing applies.</item>
/// <item><see cref="KeptByHost"/>: what the Host itself keeps up to date (recent folders and addresses, the last update
/// check, packages). A browser's copy may be older, so it is replaced by the Host's own value instead of refused.</item>
/// </list>
/// </summary>
public static class WebSettingsRules
{
    public static IReadOnlyList<string> Allowed { get; } =
    [
        // The revision the copy came with (audit 4 H1). It changes nothing by itself: a stale one only keeps the Host's
        // newer queues and schedules from being removed.
        nameof(SettingsDto.Revision),
        nameof(SettingsDto.MaxConcurrentDownloads),
        nameof(SettingsDto.DefaultQueueId),
        nameof(SettingsDto.Queues),
        nameof(SettingsDto.Schedules),
        nameof(SettingsDto.DefaultConnections),
        nameof(SettingsDto.UseCategoryFolders),
        nameof(SettingsDto.MaxConnectionsPerHost),
        nameof(SettingsDto.ConnectionProfiles),
        nameof(SettingsDto.GlobalSpeedLimitBytesPerSecond),
        nameof(SettingsDto.AutoResumeUnfinishedOnStartup),
        nameof(SettingsDto.CustomCategories),
        nameof(SettingsDto.DeleteCategoryIds),
        // Where magnets go and whether files are chosen first; turning torrents on stays with the PC (below).
        nameof(SettingsDto.TorrentRoute),
        nameof(SettingsDto.TorrentChooseFiles),
        // T14: whether torrents go to a folder for their category, like UseCategoryFolders. The torrent folder is pipe-only.
        nameof(SettingsDto.UseTorrentCategoryFolders),
        // T2 seeding: how long a finished torrent is shared and how fast it may upload. They only act on torrents the
        // PC already runs (turning torrents on, the port and the router stay with the PC), and the Host checks the values.
        nameof(SettingsDto.TorrentSeedAfterDownload),
        nameof(SettingsDto.TorrentSeedRatio),
        nameof(SettingsDto.TorrentSeedHours),
        nameof(SettingsDto.TorrentUploadLimit),
        // T3 limits: how many torrents seed at once and how many connections they make. Like the upload limit they only
        // shape what the PC already runs, and the Host checks the values.
        nameof(SettingsDto.TorrentSeedsAtOnce),
        nameof(SettingsDto.TorrentConnectionsPerTorrent),
        nameof(SettingsDto.TorrentMaxConnections),
    ];

    public static IReadOnlyList<string> PipeOnly { get; } =
    [
        nameof(SettingsDto.DefaultDownloadDirectory),
        nameof(SettingsDto.CategoryFolders),
        nameof(SettingsDto.TorrentCategoryFoldersRoot),
        nameof(SettingsDto.FavoriteFolders),
        nameof(SettingsDto.CloseWindowCompletely),
        nameof(SettingsDto.Proxy),
        nameof(SettingsDto.SiteLogins),
        nameof(SettingsDto.LaunchAtStartup),
        nameof(SettingsDto.ClipboardMonitoring),
        nameof(SettingsDto.BrowserIntegrationEnabled),
        nameof(SettingsDto.ExcludedFileExtensions),
        nameof(SettingsDto.ExcludedHosts),
        nameof(SettingsDto.InterceptedFileExtensions),
        nameof(SettingsDto.NotifyOnCompletion),
        nameof(SettingsDto.PlayCompletionSound),
        nameof(SettingsDto.EventSoundFiles),
        nameof(SettingsDto.ShowCompletionDialog),
        nameof(SettingsDto.AllowLocalNetwork),
        nameof(SettingsDto.PreventSleepWhileDownloading),
        // Turning torrents on makes the PC take connections from peers, and the handler is Windows' own setting; the
        // port and the router request open the PC to the network (and a firewall rule is made for that port).
        nameof(SettingsDto.TorrentsEnabled),
        nameof(SettingsDto.TorrentHandler),
        nameof(SettingsDto.TorrentListenPort),
        nameof(SettingsDto.TorrentUpnp),
        // T3: how this PC finds peers and shows itself to them (DHT, PEX, LSD on the local network, encryption) and
        // whether torrents go through the proxy, whose address is pipe-only too.
        nameof(SettingsDto.TorrentDht),
        nameof(SettingsDto.TorrentPex),
        nameof(SettingsDto.TorrentLsd),
        nameof(SettingsDto.TorrentEncryption),
        nameof(SettingsDto.TorrentUseProxy),
        nameof(SettingsDto.PostDownloadCommand),
        nameof(SettingsDto.UpdateManifestUrl),
        nameof(SettingsDto.UpdatePublicKey),
        nameof(SettingsDto.UpdateChannel),
        nameof(SettingsDto.AutoCheckUpdates),
        nameof(SettingsDto.UpdateCheckIntervalHours),
        nameof(SettingsDto.AllowSystemCompletionActions),
        nameof(SettingsDto.Theme),
    ];

    public static IReadOnlyList<string> KeptByHost { get; } =
    [
        nameof(SettingsDto.LastDestinationDirectory),
        nameof(SettingsDto.RecentDestinationDirectories),
        nameof(SettingsDto.RecentUrls),
        nameof(SettingsDto.SkippedUpdateVersion),
        nameof(SettingsDto.LastUpdateCheck),
        nameof(SettingsDto.DownloadGroups),
        // Worked out by the Host from the saved program (review 2:L5).
        nameof(SettingsDto.PostDownloadCommandProblem),
        // Cut in 0.7.2 and only passed through for 0.7.1 servers; this Host never has a value.
        nameof(SettingsDto.FfmpegPath),
    ];

    public const string ScheduleActionsRefused =
        "What a schedule does when it is done (open the file, exit, shut down), and a schedule that does any of these, can only be set in the app on this PC. Nothing was saved.";

    public const string CategoryFolderRefused =
        "A category's folder can only be set in the app on this PC. Nothing was saved.";

    /// <summary>
    /// Why <paramref name="requested"/> can't be saved by a web session, or null when it can. Text only: nothing here
    /// looks at the disk. Call <see cref="KeepHostOwned"/> first.
    /// </summary>
    public static string? DescribeRefusal(SettingsDto requested, SettingsDto current)
    {
        var changed = PipeOnly.Where(name => !Same(name, requested, current)).Select(JsonName).ToList();
        if (changed.Count > 0)
            return $"These settings can only be changed in the app on this PC: {string.Join(", ", changed)}. Nothing was saved.";
        if (ChangesScheduleActions(requested.Schedules, current.Schedules)) return ScheduleActionsRefused;
        if (ChangesCategoryFolders(requested.CustomCategories, current.CustomCategories)) return CategoryFolderRefused;
        return null;
    }

    /// <summary>
    /// What a web session is not shown (<see cref="ForWeb"/>): the programs the Host runs and the update source, the
    /// proxy's address and user, the site logins, the sound files, and the recent addresses and folders. They are
    /// pipe-only or kept by the Host anyway, and an address the user used can carry a token.
    /// </summary>
    public static IReadOnlyList<string> Hidden { get; } =
    [
        nameof(SettingsDto.PostDownloadCommand),
        nameof(SettingsDto.PostDownloadCommandProblem),
        nameof(SettingsDto.UpdateManifestUrl),
        nameof(SettingsDto.Proxy),
        nameof(SettingsDto.SiteLogins),
        nameof(SettingsDto.EventSoundFiles),
        nameof(SettingsDto.RecentDestinationDirectories),
        nameof(SettingsDto.RecentUrls),
    ];

    /// <summary>A copy of <paramref name="settings"/> as a web session sees it, without what is <see cref="Hidden"/>. The proxy keeps its mode.</summary>
    public static SettingsDto ForWeb(SettingsDto settings)
    {
        var copy = JsonSerializer.Deserialize<SettingsDto>(JsonSerializer.SerializeToElement(settings, IpcJson.Options), IpcJson.Options)!;
        copy.PostDownloadCommand = null;
        copy.PostDownloadCommandProblem = null;
        copy.UpdateManifestUrl = null;
        copy.Proxy = new ProxyDto { Mode = settings.Proxy?.Mode ?? "none" };
        copy.SiteLogins = [];
        copy.EventSoundFiles = new(StringComparer.OrdinalIgnoreCase);
        copy.RecentDestinationDirectories = [];
        copy.RecentUrls = [];
        return copy;
    }

    /// <summary>
    /// Puts the Host's own values in place of the browser's copy for <see cref="KeptByHost"/>, and for what is
    /// <see cref="Hidden"/> when the browser sends back what it was shown: it never had the real value to keep.
    /// </summary>
    public static void KeepHostOwned(SettingsDto requested, SettingsDto current)
    {
        // T14: a browser page from before the torrent folders sends neither; it keeps the Host's (null in a save keeps).
        requested.UseTorrentCategoryFolders ??= current.UseTorrentCategoryFolders;
        requested.TorrentCategoryFoldersRoot ??= current.TorrentCategoryFoldersRoot;

        foreach (var name in KeptByHost)
        {
            var property = typeof(SettingsDto).GetProperty(name)!;
            property.SetValue(requested, property.GetValue(current));
        }

        var shown = ForWeb(current);
        foreach (var name in Hidden.Except(KeptByHost))
        {
            if (!Same(name, requested, shown)) continue;
            var property = typeof(SettingsDto).GetProperty(name)!;
            property.SetValue(requested, property.GetValue(current));
        }
    }

    private static bool Same(string name, SettingsDto requested, SettingsDto current)
    {
        var property = typeof(SettingsDto).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!;
        var left = property.GetValue(requested);
        var right = property.GetValue(current);
        return name switch
        {
            nameof(SettingsDto.Proxy) => SameProxy(left as ProxyDto, right as ProxyDto),
            nameof(SettingsDto.SiteLogins) => SameLogins(left as List<SiteLoginDto>, right as List<SiteLoginDto>),
            _ when property.PropertyType == typeof(string) => SameText(left as string, right as string),
            _ => SameJson(left, right),
        };
    }

    /// <summary>Unchanged when the address and user are, and no password is given or cleared. A saved password is never sent out.</summary>
    private static bool SameProxy(ProxyDto? requested, ProxyDto? current)
    {
        requested ??= new ProxyDto();
        current ??= new ProxyDto();
        return string.Equals(Normal(requested.Mode) ?? "none", Normal(current.Mode) ?? "none", StringComparison.OrdinalIgnoreCase) &&
               SameText(requested.Address, current.Address) && SameText(requested.Username, current.Username) &&
               string.IsNullOrEmpty(requested.Password) && !requested.ClearPassword;
    }

    /// <summary>Null keeps them all (the Host's rule). Otherwise the same logins, sites and users, and no password given.</summary>
    private static bool SameLogins(List<SiteLoginDto>? requested, List<SiteLoginDto>? current)
    {
        if (requested is null) return true;
        if (requested.Any(login => login is null || !string.IsNullOrEmpty(login.Password))) return false;
        static IEnumerable<string> Key(IEnumerable<SiteLoginDto> logins) =>
            logins.Select(login => $"{login.Id:N}\n{Normal(login.Site)}\n{Normal(login.Username)}").Order(StringComparer.Ordinal);
        return Key(requested).SequenceEqual(Key(current ?? []), StringComparer.Ordinal);
    }

    private static bool ChangesScheduleActions(List<ScheduleDto>? requested, List<ScheduleDto>? current)
    {
        var before = (current ?? []).Where(schedule => schedule is not null).GroupBy(schedule => schedule.Id).ToDictionary(group => group.Key, group => group.First());
        var plain = new ScheduleDto();
        foreach (var schedule in requested ?? [])
        {
            if (schedule is null) continue;
            var reference = before.GetValueOrDefault(schedule.Id) ?? plain;
            if (schedule.OpenFileWhenDone != reference.OpenFileWhenDone || schedule.ExitWhenDone != reference.ExitWhenDone ||
                schedule.ShutdownWhenDone != reference.ShutdownWhenDone || schedule.ForceShutdown != reference.ForceShutdown ||
                !string.Equals(Normal(schedule.ShutdownMode) ?? plain.ShutdownMode, Normal(reference.ShutdownMode) ?? plain.ShutdownMode, StringComparison.OrdinalIgnoreCase))
                return true;
            // Audit 2 H1: a schedule that opens a file, exits or shuts down when it is done is left wholly to this PC.
            // Pointed at another queue, turned on or given other times, it would run those actions for downloads the
            // web session puts there. Removing it is allowed: that only takes the actions away.
            if (reference.OpenFileWhenDone || reference.ExitWhenDone || reference.ShutdownWhenDone)
                if (!SameTrigger(schedule, reference)) return true;
        }

        return false;
    }

    /// <summary>The same schedule apart from its run record (<c>last*</c>, which the Host keeps) and its name.</summary>
    private static bool SameTrigger(ScheduleDto requested, ScheduleDto current) =>
        requested.QueueId == current.QueueId && requested.Enabled == current.Enabled &&
        string.Equals(Normal(requested.Kind) ?? "daily", Normal(current.Kind) ?? "daily", StringComparison.OrdinalIgnoreCase) &&
        requested.StartDate == current.StartDate && SameText(requested.Days, current.Days) &&
        SameText(requested.StartTime, current.StartTime) && SameText(requested.StopTime, current.StopTime) &&
        requested.StartOnHostStartup == current.StartOnHostStartup && requested.TimedStartEnabled == current.TimedStartEnabled &&
        requested.RepeatEveryMinutes == current.RepeatEveryMinutes;

    private static bool ChangesCategoryFolders(List<CategoryDefinitionDto>? requested, List<CategoryDefinitionDto>? current)
    {
        var before = (current ?? []).Where(category => category is not null).GroupBy(category => category.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().DestinationDirectory, StringComparer.Ordinal);
        foreach (var category in requested ?? [])
        {
            if (category is null) continue;
            var folder = Normal(category.DestinationDirectory);
            var was = before.TryGetValue(category.Id ?? string.Empty, out var existing) ? Normal(existing) : null;
            if (!string.Equals(folder, was, PathNames.Comparison)) return true;
        }

        return false;
    }

    // The settings store trims texts and keeps an empty one as none, so " x" and "x", and "" and null, are one value.
    private static bool SameText(string? left, string? right) => string.Equals(Normal(left), Normal(right), StringComparison.Ordinal);

    private static string? Normal(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>The same value as JSON, the order of an object's names aside; a missing list is an empty one.</summary>
    private static bool SameJson(object? left, object? right)
    {
        var a = JsonSerializer.SerializeToElement(left, IpcJson.Options);
        var b = JsonSerializer.SerializeToElement(right, IpcJson.Options);
        if (JsonElement.DeepEquals(a, b)) return true;
        return Empty(a) && Empty(b);
    }

    private static bool Empty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => !value.EnumerateObject().Any(),
        _ => false,
    };

    private static string JsonName(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);
}
