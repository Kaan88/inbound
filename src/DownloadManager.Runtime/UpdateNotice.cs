using DownloadManager.Contracts;

namespace DownloadManager.Runtime;

/// <summary>
/// UPD.D1: the dismiss state of the "update available" notice, kept on the server per server (the server has one password
/// and no roles, so whoever signs in, or a desktop app paired as remoteClient, is the admin; a paired browser extension
/// never sees it). "Not now" (and ×) hide the notice for a day; "Skip this version" hides it until a newer version. The
/// status bar's version note is not part of this: it stays while the server is out of date, so an update is never lost.
/// </summary>
public sealed record UpdateNoticeState(DateTimeOffset? HiddenUntil, string? SkippedVersion)
{
    public static readonly UpdateNoticeState None = new(null, null);

    /// <summary>How long "Not now" (and ×) hide the notice for.</summary>
    public static readonly TimeSpan HiddenFor = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether the notice should show: a newer version is known and it is neither hidden for the day nor skipped for this
    /// version. <paramref name="now"/> is the client's clock, so "a day" is a day as whoever looks at it sees it.
    /// </summary>
    public static bool ShouldShow(string? hostVersion, string? latestVersion, UpdateNoticeState state, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(hostVersion) || string.IsNullOrWhiteSpace(latestVersion)) return false;
        if (Changelog.Compare(latestVersion, hostVersion) is not > 0) return false;
        if (state.HiddenUntil is { } until && now < until) return false;
        if (state.SkippedVersion is { } skipped && Changelog.Compare(latestVersion, skipped) is not > 0) return false;
        return true;
    }

    /// <summary>
    /// The state after a dismiss. "Not now" hides for <see cref="HiddenFor"/>; "Skip this version" skips the version that
    /// is known now. Anything else is not a dismiss and returns null.
    /// </summary>
    public static UpdateNoticeState? Dismiss(string action, string? latestVersion, UpdateNoticeState current, DateTimeOffset now)
    {
        switch (action)
        {
            case UpdateDismissActions.NotNow:
                return current with { HiddenUntil = now + HiddenFor };
            case UpdateDismissActions.Skip:
                if (string.IsNullOrWhiteSpace(latestVersion)) return null;
                return current with { SkippedVersion = latestVersion };
            default:
                return null;
        }
    }
}
