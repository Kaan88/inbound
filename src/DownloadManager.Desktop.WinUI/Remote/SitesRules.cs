using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What the rclone part of Options > Sites shows and which of its buttons work, for one status.</summary>
internal sealed record SitesRcloneView(
    string Line,
    bool LineIsProblem,
    string? OfferLine,
    bool ShowDownload,
    bool EnableDownload,
    bool EnableCheck,
    bool ShowUpdate,
    bool EnableUpdate,
    string? UpdateVersion,
    string? LastCheckLine,
    bool EnableAutoUpdate);

/// <summary>The values of the Add a site / Edit a site form, as typed.</summary>
internal sealed record SiteFormValues(
    string Kind,
    string Name,
    string Host,
    string Port,
    string User,
    string AuthMethod,
    string Password,
    string PrivateKey,
    string KeyPassphrase,
    bool RemovePassphrase,
    string RemoteFolder,
    string LocalFolder);

/// <summary>The checks and the words of Options > Sites that need no window.</summary>
internal static class SitesRules
{
    public const int MostKeyBytes = 64 * 1024;

    public static string SourceWords(string? source) => source switch
    {
        SitesRcloneSources.Own => "your own rclone",
        SitesRcloneSources.Environment => "set by INBOUND_RCLONE",
        SitesRcloneSources.Downloaded => "downloaded by Inbound",
        SitesRcloneSources.Shipped => "comes with this build",
        _ => "found on this PC",
    };

    /// <summary>
    /// No rclone to run: the Host said so after looking, or it found none it could name. Right after the switch is turned
    /// on the Host has not looked yet, so the second half is what tells.
    /// </summary>
    public static bool RcloneMissing(SitesStatusDto status) => status.Rclone == SitesRcloneStates.Missing
        || (status.RcloneSource is null && status.RcloneVersion is null && status.RcloneInstalled is null);

    /// <summary>The rclone words, the update offer and the buttons for <paramref name="status"/>.</summary>
    public static SitesRcloneView Rclone(SitesStatusDto status, DateTimeOffset now, bool busy = false)
    {
        var enabled = status.Enabled;
        var installing = status.RcloneInstalling || busy;
        var missing = RcloneMissing(status);
        var ownPath = !string.IsNullOrWhiteSpace(status.OwnRclonePath);

        string line;
        var problem = false;
        if (installing) line = "Downloading rclone…";
        // Off, the Host does not look for rclone, so not finding it is no problem yet.
        else if (missing && !enabled) line = "rclone is looked for when Sites is on.";
        else if (missing)
        {
            problem = true;
            line = ownPath
                ? "No rclone was found at the path you chose. Pick it again, or clear it to use Inbound's own."
                : "rclone isn't on this PC yet. Sites needs it.";
        }
        else
        {
            var version = status.RcloneVersion ?? status.RcloneInstalled;
            // The Host reads the version when it first starts rclone; until then only where it comes from is known.
            line = version is null
                ? $"rclone ({SourceWords(status.RcloneSource)}). Its version shows once it has started."
                : $"rclone {version} ({SourceWords(status.RcloneSource)})";
            if (enabled && status.Rclone == SitesRcloneStates.Failed)
            {
                problem = true;
                line += ". It didn't start.";
            }
        }

        if (enabled && !installing && (status.Rclone == SitesRcloneStates.Failed || status.Rclone == SitesRcloneStates.Missing)
            && !string.IsNullOrWhiteSpace(status.Error))
            line += " " + status.Error.Trim();

        var update = status.RcloneUpdate;
        var own = status.RcloneSource == SitesRcloneSources.Own;
        var offer = update is null || own ? null : OfferLine(update, status.AutoUpdateRclone, now);
        var offerable = update is not null && !own && (update.State == "ready" || update.State == "needsAsk") && update.Version.Length > 0;
        var showDownload = !ownPath && (missing || installing);

        string? last = null;
        if (!own && !string.IsNullOrWhiteSpace(status.RcloneUpdateError)) last = $"Couldn't check for a newer rclone. {status.RcloneUpdateError.Trim()}";
        else if (!own && status.RcloneLastCheck is { } checkedAt)
            last = $"Last checked {checkedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}.";

        return new SitesRcloneView(
            line,
            problem,
            offer,
            ShowDownload: showDownload,
            EnableDownload: enabled && !installing,
            EnableCheck: enabled && !installing && !own && !missing,
            ShowUpdate: offerable,
            EnableUpdate: enabled && !installing,
            UpdateVersion: offerable ? update!.Version : null,
            LastCheckLine: last,
            // Inbound never updates an rclone the user chose.
            EnableAutoUpdate: enabled && !installing && !own && !ownPath);
    }

    /// <summary>"rclone X is ready", "waits until", "is a new major version", as the update offer says.</summary>
    public static string OfferLine(SitesRcloneUpdateDto update, bool automatic, DateTimeOffset now)
    {
        var version = update.Version;
        switch (update.State)
        {
            case "ready":
                return automatic ? $"rclone {version} is ready and installs by itself." : $"rclone {version} is ready.";
            case "waiting":
                return update.WaitUntil is { } until
                    ? $"rclone {version} is out. It waits until {until.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}, a week after its release."
                    : $"rclone {version} is out. It waits a week after its release.";
            case "needsAsk":
                return $"rclone {version} is a new major version. It isn't installed by itself.";
            default:
                return $"rclone {version} is available.";
        }
    }

    /// <summary>One line for a fingerprint: its key type and its SHA256.</summary>
    public static string KeyLine(SiteHostKeyDto key) => $"{key.Type}  {key.Fingerprint}";

    public static string Summary(SiteDto site)
    {
        var where = site.Port is > 0 and not 22 ? $"{site.Host}:{site.Port}" : site.Host;
        var line = $"{(string.IsNullOrEmpty(site.User) ? "" : site.User + "@")}{where}";
        if (!string.IsNullOrEmpty(site.RemoteFolder)) line += $"  {site.RemoteFolder}";
        return line;
    }

    /// <summary>Whether a saved site needs its host key asked about: a new one, or one whose host or port changed.</summary>
    public static bool NeedsTrust(SiteDto saved) => saved.HostKeys.Count == 0;

    /// <summary>Whether the probe saw the key rclone was offered, so a replacement trusts what was actually seen.</summary>
    public static bool OffersFingerprint(IEnumerable<SiteHostKeyDto> keys, string? offered)
        => !string.IsNullOrEmpty(offered) && keys.Any(key => string.Equals(key.Fingerprint, offered, StringComparison.Ordinal));

    public static string? PortProblem(string? text, out int port)
    {
        port = 0;
        if (!int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            return "The port must be a number from 1 to 65535.";
        return null;
    }

    /// <summary>
    /// The request for a form, or null and a problem in words. In edit mode (<paramref name="existing"/> set) a secret left
    /// blank is null, which keeps the stored one; a new site needs the secret its sign-in method uses.
    /// </summary>
    public static SiteSaveRequest? Request(SiteFormValues values, SiteDto? existing, out string? problem)
    {
        problem = null;
        var name = values.Name.Trim();
        var host = values.Host.Trim();
        var user = values.User.Trim();
        if (name.Length == 0) { problem = "Give the site a name."; return null; }
        if (host.Length == 0) { problem = "Type the server's name or address."; return null; }
        if (host.Any(c => char.IsWhiteSpace(c) || c is '/' or '@' or '\\'))
        {
            problem = "The server is a name or an address such as files.example.com, without sftp:// or a user name.";
            return null;
        }

        if (PortProblem(values.Port, out var port) is { } portProblem) { problem = portProblem; return null; }
        if (user.Length == 0) { problem = "Type the user name."; return null; }

        var usesKey = values.AuthMethod == "key";
        string? password = null, key = null, passphrase = null;
        if (usesKey)
        {
            if (values.PrivateKey.Trim().Length == 0)
            {
                if (existing is null || !existing.HasPrivateKey) { problem = "Paste the private key, or choose its file."; return null; }
            }
            else
            {
                if (SiteKeyRules.Problem(values.PrivateKey) is { } keyProblem) { problem = keyProblem; return null; }
                key = Normalize(values.PrivateKey);
            }

            if (values.RemovePassphrase) passphrase = string.Empty;
            else if (values.KeyPassphrase.Length > 0) passphrase = values.KeyPassphrase;
        }
        else
        {
            if (values.Password.Length == 0)
            {
                if (existing is null || !existing.HasPassword) { problem = "Type the password."; return null; }
            }
            else password = values.Password;
        }

        return new SiteSaveRequest
        {
            Id = existing?.Id,
            Name = name,
            Kind = values.Kind,
            Host = host,
            Port = port,
            User = user,
            AuthMethod = usesKey ? "key" : "password",
            Password = password,
            PrivateKey = key,
            KeyPassphrase = passphrase,
            RemoteFolder = values.RemoteFolder.Trim(),
            LocalFolder = string.IsNullOrWhiteSpace(values.LocalFolder) ? null : values.LocalFolder.Trim(),
        };
    }

    /// <summary>A pasted key with the line ends the SSH tools expect and a closing line end.</summary>
    private static string Normalize(string text) => text.Trim().Replace("\r\n", "\n").Replace('\r', '\n') + "\n";
}
