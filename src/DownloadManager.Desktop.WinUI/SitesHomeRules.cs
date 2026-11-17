using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One tile of the Add a remote dialog: what it is called, what it says, and the Host kinds it stands for.</summary>
internal sealed record SiteTile(string Id, string Name, string Line, string Glyph, string[] Kinds, string FormTitle);

/// <summary>
/// The counts of a remote's sidebar entry and All remotes row (SITES.12 R4): files moving by direction, batches waiting,
/// the bytes done of the batches that move, and how many batches move (<paramref name="Running"/>).
/// </summary>
internal sealed record SiteBadge(int Down, int Up, int Waiting, double? Progress, int Running = 0);

/// <summary>What opening an address once does next after the Host's answer to a browse (SITES.11 R4).</summary>
internal enum OnceStep
{
    /// <summary>Nothing to ask: the answer stands.</summary>
    Done,

    /// <summary>The unsaved remote needs a password: ask, and send it with the remote's id.</summary>
    AskPassword,
}

/// <summary>
/// SITES.11 R4: the words and choices of the All remotes page, the Add a remote dialog and the sidebar entries, with no
/// UI in them. Names follow SITES.12: a place is "a remote", the whole is "Remote files".
/// </summary>
internal static class SitesHomeRules
{
    public const string Heading = "Remote files";
    public const string AllRemotes = "All remotes";
    public const string AddRemote = "Add a remote";
    public const string OpenHint = "Open an address once, for example sftp://nas.home.lan/media";

    // Segoe Fluent glyphs: a server, a cloud.
    private const string Server = "";
    private const string Cloud = "";

    /// <summary>The tiles in the order drawn. "Ask each time" and "More…" are not here: the Host has no such sign-in or kind.</summary>
    public static IReadOnlyList<SiteTile> Tiles { get; } =
    [
        new("sftp", "SFTP", "A server you sign in to with SSH", Server, [SiteKindCatalog.Sftp], "Add an SFTP remote"),
        new("ftp", "FTP", "Older servers, with or without TLS", Server, [SiteKindCatalog.Ftp], "Add an FTP remote"),
        new("webdav", "WebDAV", "Nextcloud, ownCloud, Synology", Cloud, [SiteKindCatalog.WebDav], "Add a WebDAV remote"),
        new("drive", "Google Drive", "Signs in through your browser", Cloud, [SiteKindCatalog.Drive], "Add a Google Drive remote"),
        new("onedrive", "OneDrive", "Signs in through your browser", Cloud, [SiteKindCatalog.OneDrive], "Add a OneDrive remote"),
        new("dropbox", "Dropbox", "Signs in through your browser", Cloud, [SiteKindCatalog.Dropbox], "Add a Dropbox remote"),
        new("s3b2", "S3 or B2", "Access key and bucket", Cloud, [SiteKindCatalog.S3, SiteKindCatalog.B2], "Add an S3 or B2 remote"),
    ];

    /// <summary>The tiles whose kinds the Host offers (none listed: an older Host, which offers SFTP).</summary>
    public static IReadOnlyList<SiteTile> TilesFor(IEnumerable<SiteKindDto> hostKinds)
    {
        var offered = hostKinds.Select(k => k.Kind).ToHashSet(StringComparer.Ordinal);
        if (offered.Count == 0) offered.Add(SiteKindCatalog.Sftp);
        return Tiles.Where(tile => tile.Kinds.Any(offered.Contains)).ToList();
    }

    /// <summary>The tile a remote of <paramref name="kind"/> belongs to.</summary>
    public static SiteTile? TileOf(string kind) => Tiles.FirstOrDefault(tile => tile.Kinds.Contains(kind, StringComparer.Ordinal));

    /// <summary>A kind's small chip: "SFTP", "Drive", "WebDAV".</summary>
    public static string KindText(string kind) => kind switch
    {
        SiteKindCatalog.Sftp => "SFTP",
        SiteKindCatalog.Ftp => "FTP",
        SiteKindCatalog.WebDav => "WebDAV",
        SiteKindCatalog.S3 => "S3",
        SiteKindCatalog.B2 => "B2",
        SiteKindCatalog.Drive => "Drive",
        SiteKindCatalog.OneDrive => "OneDrive",
        SiteKindCatalog.Dropbox => "Dropbox",
        _ => kind,
    };

    /// <summary>The name a remote gets when none is typed: its server, or the service's own name.</summary>
    public static string DefaultName(string kind, string host, string? url, string? endpoint)
    {
        var address = !string.IsNullOrWhiteSpace(host) ? host.Trim() : HostOf(url) ?? HostOf(endpoint);
        return string.IsNullOrWhiteSpace(address) ? TileOf(kind)?.Name ?? KindText(kind) : address;
    }

    private static string? HostOf(string? address)
        => Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    /// <summary>"Today 09:14", "Yesterday 22:15", "Nov 10" (with the year when it is not this one); "Never" before first use.</summary>
    public static string LastUsedText(DateTimeOffset? usedAt, DateTimeOffset now)
    {
        if (usedAt is not { } at) return "Never";
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return $"Today {time}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday {time}";
        return local.Year == today.Year
            ? local.ToString("MMM d", CultureInfo.InvariantCulture)
            : local.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What runs on a remote: files moving by direction, batches waiting, and the bytes done of those that move. A batch is
    /// waiting by the Running tab's rule (<see cref="SiteTransferRules.IsStarted"/>): not started yet, or waiting to try its
    /// server again; one between files is not.
    /// </summary>
    public static SiteBadge Badge(IEnumerable<SiteTransferDto> transfers, string siteId)
    {
        var running = transfers.Where(t => t.SiteId == siteId && t.State == "running").ToList();
        if (running.Count == 0) return new SiteBadge(0, 0, 0, null);
        var started = running.Where(SiteTransferRules.IsStarted).ToList();
        var down = started.Where(SiteTransferRules.IsDownload).Sum(t => t.FilesRunning);
        var up = started.Where(t => !SiteTransferRules.IsDownload(t)).Sum(t => t.FilesRunning);
        // While batches move there is always a line: by bytes, or by files when no size is known yet, or empty.
        var total = started.Sum(t => t.Bytes);
        var files = started.Sum(t => t.Files);
        double? progress = started.Count == 0 ? null
            : total > 0 ? Math.Clamp(100.0 * started.Sum(t => t.BytesDone) / total, 0, 100)
            : files > 0 ? Math.Clamp(100.0 * started.Sum(t => t.FilesDone + t.FilesSkipped + t.FilesFailed) / files, 0, 100)
            : 0;
        return new SiteBadge(down, up, running.Count - started.Count, progress, started.Count);
    }

    /// <summary>The arrows part of a sidebar entry: "↓1 ↑1", or empty.</summary>
    public static string ArrowsText(SiteBadge badge)
        => string.Join(" ", new[] { badge.Down > 0 ? $"↓{badge.Down}" : null, badge.Up > 0 ? $"↑{badge.Up}" : null }.OfType<string>());

    /// <summary>The count beside the sidebar entry's clock glyph while batches wait, or empty (no clock then).</summary>
    public static string WaitingCount(SiteBadge badge) => badge.Waiting > 0 ? badge.Waiting.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>The All remotes row's right-hand text: "2 transfers running" (batches, not files), "1 waiting"; null when idle.</summary>
    public static string? ActivityText(SiteBadge badge)
    {
        if (badge.Running > 0) return badge.Running == 1 ? "1 transfer running" : $"{badge.Running} transfers running";
        return badge.Waiting > 0 ? $"{badge.Waiting} waiting" : null;
    }

    /// <summary>The screen reader's words for an entry; null when idle.</summary>
    public static string? ActivityWords(SiteBadge badge)
    {
        var parts = new List<string>();
        if (badge.Down > 0) parts.Add($"{badge.Down} downloading");
        if (badge.Up > 0) parts.Add($"{badge.Up} uploading");
        if (badge.Waiting > 0) parts.Add($"{badge.Waiting} waiting");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>The status bar on the All remotes page.</summary>
    public static string FooterText(int count) => count switch
    {
        0 => "No remotes yet",
        1 => "1 remote",
        _ => $"{count} remotes",
    };

    /// <summary>True when the dot is a warning: the Host could not reach it last, its key is not trusted yet, or a sign-in is missing.</summary>
    public static bool InTrouble(SiteDto site)
        => site.Reach == SiteReach.Trouble || SitesRules.NeedsTrust(site) || (SiteKindCatalog.SignsIn(site.Kind) && !site.SignedIn);

    /// <summary>The words of a dot, for a screen reader and a tooltip.</summary>
    public static string DotWords(SiteDto site)
    {
        if (site.Reach == SiteReach.Trouble) return "Could not be reached";
        if (SiteKindCatalog.SignsIn(site.Kind) && !site.SignedIn) return "Not signed in yet";
        if (SitesRules.NeedsTrust(site)) return "The server's key isn't trusted yet";
        return "Fine";
    }

    /// <summary>The Test line's good outcome: "It works. Signed in and listed the start folder (6 items) in 0.4 s."</summary>
    public static string TestWorks(bool signsIn, TimeSpan took, int? items = null)
        => $"It works. {(signsIn ? "Signed in" : "Connected")} and listed the start folder{ItemsText(items)} in {took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s.";

    private static string ItemsText(int? items) => items switch
    {
        null => "",
        1 => " (1 item)",
        _ => $" ({items.Value.ToString("N0", CultureInfo.InvariantCulture)} items)",
    };

    /// <summary>
    /// The Test line for the Host's answer to the form's draft (<c>sites.testDraft</c>). A server key or a certificate is
    /// shown under the line with a Trust button; a sign-in follows the save.
    /// </summary>
    public static string DraftTestText(SiteResultDto result, bool signsIn, TimeSpan took)
    {
        if (result.Ok) return TestWorks(signsIn, took, result.ItemCount);
        return result.Problem switch
        {
            SiteProblems.HostKeyNotAccepted when result.OfferedKeys is { Count: > 0 } =>
                "The server's key isn't trusted yet. Compare it with the one your server's administrator gave you, then press Trust.",
            SiteProblems.HostKeyNotAccepted => "The server's key isn't trusted yet, and it couldn't be read.",
            SiteProblems.HostKeyChanged => "The server shows a different key from the one you trusted. Save, then Test the remote to see it.",
            SiteProblems.CertificateNotTrusted when result.Certificate is { CannotTrust: { } cannot } => $"The server's certificate can't be used. {cannot}",
            SiteProblems.CertificateNotTrusted when result.Certificate is not null =>
                "The server's certificate isn't signed by an authority this PC trusts. Trust it only if it is your own server's; it is trusted when you save.",
            SiteProblems.CertificateNotTrusted => "The server's certificate isn't trusted yet: press Save, and Inbound shows it to you.",
            SiteProblems.SignInNeeded => "Not signed in yet: press Save, and your browser opens to sign in.",
            _ => string.IsNullOrWhiteSpace(result.Message) ? "The test failed." : result.Message,
        };
    }

    /// <summary>
    /// True when the Host has no such request (it is older): a Host answers an unknown Sites request with "Unknown Sites
    /// request …", and one that does not know the message at all with another reply type (<paramref name="doesNotOffer"/>).
    /// </summary>
    public static bool HostLacks(string? message, string? error, string doesNotOffer)
        => error == doesNotOffer || (message?.StartsWith("Unknown Sites request", StringComparison.Ordinal) ?? false);

    // ---- The form's request ------------------------------------------------------------------------------

    /// <summary>The name a remote is saved with: as typed, or its server's (or service's) name when nothing is.</summary>
    public static string FormName(string typed, string kind, string host, string? url, string? endpoint)
        => typed.Trim().Length > 0 ? typed : DefaultName(kind, host, url, endpoint);

    /// <summary>A key file's path as typed or pasted (Explorer's "Copy as path" adds quotes).</summary>
    public static string KeyFilePath(string typed) => typed.Trim().Trim('"');

    /// <summary>Why a key file can't be used before it is read, or null.</summary>
    public static string? KeyFileProblem(bool exists, long length)
        => !exists ? "The key file wasn't found." : length > SitesRules.MostKeyBytes ? "That file is too big to be a private key." : null;

    /// <summary>
    /// The server keys accepted in the form are for the server and port they were read from: any change to where the form
    /// points drops them (and a certificate to trust), so a key is never trusted for another server.
    /// </summary>
    public static bool SameServer((string Host, string Port, string Url, string Endpoint, string? Tls) then, (string Host, string Port, string Url, string Endpoint, string? Tls) now)
        => string.Equals(then.Host.Trim(), now.Host.Trim(), StringComparison.OrdinalIgnoreCase) && then.Port.Trim() == now.Port.Trim()
            && then.Url.Trim() == now.Url.Trim() && then.Endpoint.Trim() == now.Endpoint.Trim() && then.Tls == now.Tls;

    // ---- Open once ---------------------------------------------------------------------------------------

    /// <summary>The Host holds at most this many addresses opened once (SiteStore.MaxUnsaved).</summary>
    public const int MostOpenOnce = 8;

    /// <summary>The line after the oldest addresses opened once were closed to make room: "Closed nas.home.lan to make room."</summary>
    public static string ClosedToMakeRoom(IReadOnlyList<string> names) => $"Closed {string.Join(", ", names)} to make room.";

    /// <summary>
    /// The unsaved remotes to forget before another address is opened, so no more than <paramref name="most"/> are open:
    /// the oldest first (<paramref name="open"/> is oldest first), never the one shown and never one with batches that are
    /// not finished (<paramref name="busy"/>: forgetting it would cancel them). Null when not enough can be forgotten: the
    /// address is then not opened.
    /// </summary>
    public static IReadOnlyList<string>? ForgetBeforeOpening(IReadOnlyList<SiteDto> open, string? shownId, IReadOnlySet<string> busy, int most = MostOpenOnce)
    {
        var unsaved = open.Where(site => site.Unsaved).DistinctBy(site => site.Id).ToList();
        var over = unsaved.Count - (most - 1);
        if (over <= 0) return [];
        var free = unsaved.Where(site => site.Id != shownId && !busy.Contains(site.Id)).Take(over).Select(site => site.Id).ToList();
        return free.Count < over ? null : free;
    }

    /// <summary>What the page says when every open address is in use.</summary>
    public const string CloseOneFirst = "Close an opened address first.";

    /// <summary>The remotes with batches that are not finished: running (waiting among them) or paused.</summary>
    public static IReadOnlySet<string> BusyRemotes(IEnumerable<SiteTransferDto> transfers)
        => transfers.Where(t => t.State is "running" or "paused").Select(t => t.SiteId).ToHashSet(StringComparer.Ordinal);

    /// <summary>What follows a browse of a remote: an unsaved one that needs its password asks for it.</summary>
    public static OnceStep AfterBrowse(string? problem, bool unsaved)
        => unsaved && problem == SiteProblems.PasswordNeeded ? OnceStep.AskPassword : OnceStep.Done;

    /// <summary>An address opened once is not edited (the Host keeps it in memory only); it can be remembered instead.</summary>
    public static bool CanEdit(SiteDto site) => !site.Unsaved;

    /// <summary>"Forget the certificate" is offered for a remote that trusts a certificate of its own.</summary>
    public static bool CanForgetCertificate(SiteDto site) => site.TrustedCertificates.Count > 0;

    /// <summary>"Sign in with" choices of a kind, by the Host's method names; "Ask each time" is not one: the Host cannot ask.</summary>
    public static string MethodLabel(string method) => method == "key" ? "Key file" : "Password";

    /// <summary>The closed Advanced row. The Host has no per-remote transfer or connection limits, so they are not named.</summary>
    public static string AdvancedLabel(bool hasPort) => hasPort ? "Advanced: name, port, starting folder" : "Advanced: name, starting folder";
}
