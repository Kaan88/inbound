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
/// <item><see cref="Allowed"/>: downloads, connections, speed, queues and schedule times, and categories' names and
/// file types. Some carry a rule inside: a schedule's when-done actions and a category's folder stay as they are.</item>
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
    ];

    public static IReadOnlyList<string> PipeOnly { get; } =
    [
        nameof(SettingsDto.DefaultDownloadDirectory),
        nameof(SettingsDto.CategoryFolders),
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
        nameof(SettingsDto.FfmpegPath),
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
    ];

    public const string ScheduleActionsRefused =
        "What a schedule does when it is done (open the file, exit, shut down) can only be set in the app on this PC. Nothing was saved.";

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

    /// <summary>Puts the Host's own values in place of the browser's copy for <see cref="KeptByHost"/>.</summary>
    public static void KeepHostOwned(SettingsDto requested, SettingsDto current)
    {
        foreach (var name in KeptByHost)
        {
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
        }

        return false;
    }

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
