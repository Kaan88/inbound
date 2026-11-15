namespace DownloadManager.Contracts;

/// <summary>The fields a kind's Add a site dialog shows (<see cref="SiteKindDto.Fields"/>).</summary>
public static class SiteFields
{
    public const string Host = "host";
    public const string Port = "port";
    public const string User = "user";
    public const string Password = "password";
    public const string Key = "key";
    public const string Url = "url";
    public const string Tls = "tls";
    public const string Vendor = "vendor";
    public const string Provider = "provider";
    public const string Endpoint = "endpoint";
    public const string Region = "region";
}

/// <summary>What a kind of site can do (<see cref="SiteKindDto.Capabilities"/>); a client hides a command its kind lacks.</summary>
public static class SiteCapabilities
{
    /// <summary>New folder: an object store has no folder of its own until something is in it.</summary>
    public const string NewFolder = "newFolder";

    /// <summary>An empty folder can exist (and is made by an upload).</summary>
    public const string EmptyFolders = "emptyFolders";

    /// <summary>The server's SSH host key is asked about before first use.</summary>
    public const string HostKey = "hostKey";
}

/// <summary>The values of <see cref="SiteDto.Tls"/> for FTP.</summary>
public static class SiteTlsModes
{
    /// <summary>AUTH TLS on the normal port (21): the usual FTPS.</summary>
    public const string Explicit = "explicit";

    /// <summary>TLS from the first byte, usually on port 990.</summary>
    public const string Implicit = "implicit";

    /// <summary>Plain FTP: nothing is encrypted, the password included.</summary>
    public const string None = "none";
}

/// <summary>
/// SITES.6: the kinds of site Inbound offers, with their fields, choices and capabilities, so every client draws the same
/// dialog (remote-sites.md, "Capabilities"). The Host serves this list in <c>sites.kinds</c> and checks a save against it.
/// Choice values are rclone's own option values.
/// </summary>
public static class SiteKindCatalog
{
    public const string Sftp = "sftp";
    public const string Ftp = "ftp";
    public const string WebDav = "webdav";
    public const string S3 = "s3";

    /// <summary>S3 providers whose endpoint rclone knows without one being typed.</summary>
    public const string AwsProvider = "AWS";

    public static IReadOnlyList<SiteKindDto> All { get; } =
    [
        new()
        {
            Kind = Sftp, Title = "SFTP", DefaultPort = 22, AuthMethods = ["password", "key"],
            Fields = [SiteFields.Host, SiteFields.Port, SiteFields.User, SiteFields.Password, SiteFields.Key],
            Capabilities = [SiteCapabilities.NewFolder, SiteCapabilities.EmptyFolders, SiteCapabilities.HostKey],
        },
        new()
        {
            Kind = Ftp, Title = "FTP", DefaultPort = 21, AuthMethods = ["password"],
            Fields = [SiteFields.Host, SiteFields.Port, SiteFields.Tls, SiteFields.User, SiteFields.Password],
            Capabilities = [SiteCapabilities.NewFolder, SiteCapabilities.EmptyFolders],
            Choices = new()
            {
                [SiteFields.Tls] =
                [
                    new() { Value = SiteTlsModes.Explicit, Title = "Encrypted (explicit TLS, FTPS)" },
                    new() { Value = SiteTlsModes.Implicit, Title = "Encrypted (implicit TLS, usually port 990)" },
                    new() { Value = SiteTlsModes.None, Title = "Not encrypted (plain FTP)" },
                ],
            },
        },
        new()
        {
            Kind = WebDav, Title = "WebDAV", AuthMethods = ["password"],
            Fields = [SiteFields.Url, SiteFields.Vendor, SiteFields.User, SiteFields.Password],
            Capabilities = [SiteCapabilities.NewFolder, SiteCapabilities.EmptyFolders],
            Choices = new()
            {
                [SiteFields.Vendor] =
                [
                    new() { Value = "other", Title = "Other WebDAV server" },
                    new() { Value = "nextcloud", Title = "Nextcloud" },
                    new() { Value = "owncloud", Title = "ownCloud" },
                ],
            },
        },
        new()
        {
            Kind = S3, Title = "S3", AuthMethods = ["password"], UserLabel = "Access key ID", PasswordLabel = "Secret access key",
            Fields = [SiteFields.Provider, SiteFields.Endpoint, SiteFields.Region, SiteFields.User, SiteFields.Password],
            Capabilities = [],
            Note = "Folders exist on an object store only while they hold something, and a rename copies and then deletes.",
            Choices = new()
            {
                [SiteFields.Provider] =
                [
                    new() { Value = AwsProvider, Title = "Amazon S3" },
                    new() { Value = "Cloudflare", Title = "Cloudflare R2" },
                    new() { Value = "Wasabi", Title = "Wasabi" },
                    new() { Value = "Minio", Title = "MinIO" },
                    new() { Value = "Other", Title = "Other S3-compatible service" },
                ],
            },
        },
    ];

    public static SiteKindDto? Find(string? kind) => All.FirstOrDefault(k => k.Kind == kind);

    public static bool Has(string? kind, string capability) => Find(kind)?.Capabilities.Contains(capability) == true;

    /// <summary>Whether <paramref name="value"/> is one of the kind's choices for <paramref name="field"/>.</summary>
    public static bool IsChoice(string kind, string field, string? value) =>
        value is not null && Find(kind)?.Choices.GetValueOrDefault(field)?.Any(choice => choice.Value == value) == true;

    /// <summary>
    /// Whether the connection is encrypted: SFTP always; FTP unless plain; WebDAV and S3 when their address is https (Amazon
    /// S3 with no endpoint typed uses https).
    /// </summary>
    public static bool Encrypted(string kind, string? tls, string? url, string? endpoint) => kind switch
    {
        Sftp => true,
        Ftp => tls != SiteTlsModes.None,
        WebDav => IsHttps(url),
        S3 => string.IsNullOrWhiteSpace(endpoint) || IsHttps(endpoint),
        _ => false,
    };

    private static bool IsHttps(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Why <paramref name="address"/> can't be a site's web address (WebDAV URL, S3 endpoint), or null: http or https,
    /// absolute, with no user name or password, query or fragment in it.
    /// </summary>
    public static string? AddressProblem(string? address, string what)
    {
        if (string.IsNullOrWhiteSpace(address)) return $"Type the {what}.";
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
            return $"The {what} must start with https:// (or http://), such as https://files.example.com/remote.php/dav/files/me.";
        if (uri.UserInfo.Length > 0) return $"Leave the user name and password out of the {what}; they have boxes of their own.";
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return $"The {what} can't have a ? or # part.";
        return null;
    }
}
