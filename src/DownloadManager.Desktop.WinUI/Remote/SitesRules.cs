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
    string LocalFolder,
    string? Tls = null,
    string Url = "",
    string? Vendor = null,
    string? Provider = null,
    string Endpoint = "",
    string Region = "",
    string ClientId = "",
    string ClientSecret = "");

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
        // SITES.6: an address kind is shown by its address; S3 by its service and endpoint.
        if (site.Kind == SiteKindCatalog.WebDav && site.Url is { Length: > 0 } url)
            return string.IsNullOrEmpty(site.RemoteFolder) ? url : $"{url}  {site.RemoteFolder}";
        if (site.Kind == SiteKindCatalog.S3)
        {
            var service = SiteKindCatalog.Find(site.Kind)?.Choices.GetValueOrDefault(SiteFields.Provider)?.FirstOrDefault(c => c.Value == site.Provider)?.Title ?? "S3";
            var s3 = site.Endpoint is { Length: > 0 } endpoint ? $"{service}, {endpoint}" : site.Region is { Length: > 0 } region ? $"{service}, {region}" : service;
            return string.IsNullOrEmpty(site.RemoteFolder) ? s3 : $"{s3}  {site.RemoteFolder}";
        }

        if (SiteKindCatalog.SignsIn(site.Kind) || site.Kind == SiteKindCatalog.B2)
        {
            // SITES.6c: the service by name; its fixed API address says nothing.
            var service = SiteKindCatalog.Find(site.Kind)?.Title ?? site.Kind;
            if (site.ClientId is not null) service += ", your own app registration";
            else if (site.Kind == SiteKindCatalog.B2 && site.User.Length > 0) service += $", key {site.User}";
            return string.IsNullOrEmpty(site.RemoteFolder) ? service : $"{service}  {site.RemoteFolder}";
        }

        var defaultPort = SiteKindCatalog.Find(site.Kind)?.DefaultPort ?? 22;
        var where = site.Port > 0 && site.Port != defaultPort ? $"{site.Host}:{site.Port}" : site.Host;
        var line = $"{(string.IsNullOrEmpty(site.User) ? "" : site.User + "@")}{where}";
        if (!string.IsNullOrEmpty(site.RemoteFolder)) line += $"  {site.RemoteFolder}";
        return line;
    }

    /// <summary>Whether a saved site needs its host key asked about: a new one, or one whose host or port changed.</summary>
    public static bool NeedsTrust(SiteDto saved) => saved.Kind == SiteKindCatalog.Sftp && saved.HostKeys.Count == 0;

    /// <summary>SITES.6b: the lines of the "Trust this certificate?" question, label and value.</summary>
    public static IReadOnlyList<(string Label, string Value)> CertificateLines(SiteCertificateDto certificate)
    {
        var lines = new List<(string, string)>
        {
            ("Issued to", certificate.Names.Count > 0 ? string.Join(", ", certificate.Names) : certificate.Subject),
            ("Issued by", certificate.SelfSigned ? "itself (a self-signed certificate)" : certificate.Issuer),
            ("Valid", $"{certificate.NotBefore.ToLocalTime():d} to {certificate.NotAfter.ToLocalTime():d}"),
            ("SHA-256 fingerprint", certificate.Fingerprint),
        };
        return lines;
    }

    /// <summary>The line under a site in Options that trusts its server's own certificate, or null.</summary>
    public static string? TrustedCertificateLine(SiteDto site) => site.TrustedCertificates switch
    {
        [] => null,
        [var one] => $"Trusts its server's own certificate (valid until {one.NotAfter.ToLocalTime():d}).",
        var many => $"Trusts {many.Count} certificates of its server.",
    };

    /// <summary>
    /// SITES.6: the warning a form shows for a connection that is not encrypted, or null. Plain FTP and an http:// address
    /// send the password and the files readable to anyone on the way.
    /// </summary>
    public static string? PlainWarning(string kind, string? tls, string? url, string? endpoint) =>
        SiteKindCatalog.Encrypted(kind, tls, url, endpoint) ? null
        : kind == SiteKindCatalog.Ftp
            ? "Plain FTP is not encrypted: the password and the files can be read by anyone on the network between here and the server. Use it only on a network you trust."
            : "This address starts with http://, so the connection is not encrypted: the password and the files can be read on the way. Use https:// if the server offers it.";

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
        if (values.Kind is SiteKindCatalog.WebDav or SiteKindCatalog.S3) return AddressRequest(values, existing, name, user, out problem);
        if (SiteKindCatalog.SignsIn(values.Kind)) return SignInRequest(values, existing, name, out problem);
        if (values.Kind == SiteKindCatalog.B2) return KeyRequest(values, existing, name, user, out problem);
        if (host.Length == 0) { problem = "Type the server's name or address."; return null; }
        if (host.Any(c => char.IsWhiteSpace(c) || c is '/' or '@' or '\\'))
        {
            problem = $"The server is a name or an address such as files.example.com, without {values.Kind}:// or a user name.";
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
            Tls = values.Kind == SiteKindCatalog.Ftp ? values.Tls ?? SiteTlsModes.Explicit : null,
        };
    }

    /// <summary>SITES.6: WebDAV and S3, which are reached by an address rather than a server name and port.</summary>
    private static SiteSaveRequest? AddressRequest(SiteFormValues values, SiteDto? existing, string name, string user, out string? problem)
    {
        problem = null;
        var s3 = values.Kind == SiteKindCatalog.S3;
        var url = values.Url.Trim();
        var endpoint = values.Endpoint.Trim();
        var region = values.Region.Trim();
        if (s3)
        {
            if (values.Provider is null) { problem = "Choose the S3 service."; return null; }
            if (endpoint.Length > 0 || values.Provider != SiteKindCatalog.AwsProvider)
            {
                if (SiteKindCatalog.AddressProblem(endpoint, "service address (endpoint)") is { } endpointProblem) { problem = endpointProblem; return null; }
            }

            if (region.Length > 0 && !region.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                problem = "A region is letters, digits and dashes, such as eu-central-1.";
                return null;
            }

            if (user.Length == 0) { problem = "Type the access key ID."; return null; }
        }
        else if (SiteKindCatalog.AddressProblem(url, "server address") is { } urlProblem)
        {
            problem = urlProblem;
            return null;
        }

        string? password = null;
        if (values.Password.Length > 0) password = values.Password;
        else if (s3 && (existing is null || !existing.HasPassword)) { problem = "Type the secret access key."; return null; }

        return new SiteSaveRequest
        {
            Id = existing?.Id,
            Name = name,
            Kind = values.Kind,
            User = user,
            AuthMethod = "password",
            Password = password,
            RemoteFolder = values.RemoteFolder.Trim(),
            LocalFolder = string.IsNullOrWhiteSpace(values.LocalFolder) ? null : values.LocalFolder.Trim(),
            Url = s3 ? null : url,
            Vendor = s3 ? null : values.Vendor ?? "other",
            Provider = s3 ? values.Provider : null,
            Endpoint = s3 && endpoint.Length > 0 ? endpoint : null,
            Region = s3 && region.Length > 0 ? region : null,
        };
    }

    /// <summary>SITES.6c: Drive, OneDrive, Dropbox. No password; an own app registration is optional.</summary>
    private static SiteSaveRequest? SignInRequest(SiteFormValues values, SiteDto? existing, string name, out string? problem)
    {
        problem = null;
        var clientId = values.ClientId.Trim();
        if (clientId.Length > 256 || !clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            problem = "A client ID is letters, digits, dots, dashes and underscores.";
            return null;
        }

        if (clientId.Length == 0 && values.ClientSecret.Length > 0) { problem = "A client secret needs its client ID."; return null; }
        return new SiteSaveRequest
        {
            Id = existing?.Id,
            Name = name,
            Kind = values.Kind,
            AuthMethod = "signIn",
            RemoteFolder = values.RemoteFolder.Trim(),
            LocalFolder = string.IsNullOrWhiteSpace(values.LocalFolder) ? null : values.LocalFolder.Trim(),
            ClientId = clientId.Length == 0 ? null : clientId,
            // Empty keeps the stored secret (null on the wire) while the ID stays; the Host drops it with a new ID.
            ClientSecret = values.ClientSecret.Length > 0 ? values.ClientSecret : clientId.Length == 0 ? "" : null,
        };
    }

    /// <summary>SITES.6c, Backblaze B2: an application key ID and key, no address.</summary>
    private static SiteSaveRequest? KeyRequest(SiteFormValues values, SiteDto? existing, string name, string user, out string? problem)
    {
        problem = null;
        if (user.Length == 0) { problem = "Type the application key ID."; return null; }
        if (values.Password.Length == 0 && (existing is null || !existing.HasPassword)) { problem = "Type the application key."; return null; }
        return new SiteSaveRequest
        {
            Id = existing?.Id,
            Name = name,
            Kind = values.Kind,
            User = user,
            AuthMethod = "password",
            Password = values.Password.Length > 0 ? values.Password : null,
            RemoteFolder = values.RemoteFolder.Trim(),
            LocalFolder = string.IsNullOrWhiteSpace(values.LocalFolder) ? null : values.LocalFolder.Trim(),
        };
    }

    /// <summary>A pasted key with the line ends the SSH tools expect and a closing line end.</summary>
    private static string Normalize(string text) => text.Trim().Replace("\r\n", "\n").Replace('\r', '\n') + "\n";
}
