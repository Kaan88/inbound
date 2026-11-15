using System.Globalization;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Persistence;

namespace DownloadManager.Sites;

/// <summary>
/// The rclone remote for one call, built from a saved site (remote-sites.md, "Credentials and trust"; SITES.0 points 1, 3,
/// 5): an <c>fs</c> object of backend options, never a config file. Secrets are opened here, registered with the client as
/// secrets (so no error text carries them), and passwords and passphrases are obscured with <c>core/obscure</c>.
/// </summary>
public static class SiteRemote
{
    /// <summary>
    /// The <c>fs</c> object. Throws <see cref="SiteStoreException"/> when the site can't be used as it is: no accepted host key
    /// (the first-use prompt has not been answered), or a secret that does not open on this PC.
    /// </summary>
    public static async Task<JsonObject> BuildAsync(SiteRecord site, RcloneClient client, CancellationToken cancellationToken = default) => site.Kind switch
    {
        SiteKinds.Sftp => await SftpAsync(site, client, cancellationToken).ConfigureAwait(false),
        SiteKinds.Ftp => await FtpAsync(site, client, cancellationToken).ConfigureAwait(false),
        SiteKinds.WebDav => await WebDavAsync(site, client, cancellationToken).ConfigureAwait(false),
        SiteKinds.S3 => S3(site, client),
        _ => throw new SiteStoreException($"Sites of kind \"{site.Kind}\" are not supported."),
    };

    private static async Task<JsonObject> SftpAsync(SiteRecord site, RcloneClient client, CancellationToken cancellationToken)
    {
        // rclone does not check an SFTP host key unless told to (SITES.0 point 3), so a site without an accepted key never
        // reaches it: the window asks first.
        if (site.NeedsHostKey) throw new SiteStoreException("The server's key has not been accepted yet.");

        var fs = new JsonObject
        {
            ["type"] = "sftp",
            ["host"] = site.Host,
            ["port"] = site.Port.ToString(CultureInfo.InvariantCulture),
            ["user"] = site.User,
            ["host_keys"] = string.Join(',', site.HostKeys),
            // Absolute paths (SITES.0 point 1): the remote starts at the server's root, not the user's home.
            ["_root"] = "/",
        };

        if (site.AuthMethod == SiteAuthMethods.Key)
        {
            if (site.SealedPrivateKey is null) throw new SiteStoreException("No private key is saved for this site; choose one in Edit site.");
            var key = Open(site.SealedPrivateKey, "private key");
            client.AddSecret(key);
            // key_pem wants its line breaks as literal \n (SITES.0 point 5).
            var pem = key.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\\n", StringComparison.Ordinal);
            client.AddSecret(pem);
            fs["key_pem"] = pem;
            if (site.SealedKeyPassphrase is not null)
                fs["key_file_pass"] = await client.ObscureAsync(Open(site.SealedKeyPassphrase, "key passphrase"), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (site.SealedPassword is not null)
                fs["pass"] = await client.ObscureAsync(Open(site.SealedPassword, "password"), cancellationToken).ConfigureAwait(false);
        }

        return fs;
    }

    /// <summary>FTP: explicit TLS (<c>explicit_tls</c>), implicit TLS (<c>tls</c>) or plain. A certificate is checked; it is never switched off.</summary>
    private static async Task<JsonObject> FtpAsync(SiteRecord site, RcloneClient client, CancellationToken cancellationToken)
    {
        var fs = new JsonObject
        {
            ["type"] = "ftp",
            ["host"] = site.Host,
            ["port"] = site.Port.ToString(CultureInfo.InvariantCulture),
            ["user"] = site.User,
            ["tls"] = site.Tls == SiteTlsModes.Implicit ? "true" : "false",
            ["explicit_tls"] = site.Tls == SiteTlsModes.Explicit ? "true" : "false",
            ["_root"] = "/",
        };
        if (site.SealedPassword is not null)
            fs["pass"] = await client.ObscureAsync(Open(site.SealedPassword, "password"), cancellationToken).ConfigureAwait(false);
        return fs;
    }

    /// <summary>WebDAV: the site's address is the root; paths below it are the folders shown.</summary>
    private static async Task<JsonObject> WebDavAsync(SiteRecord site, RcloneClient client, CancellationToken cancellationToken)
    {
        var fs = new JsonObject
        {
            ["type"] = "webdav",
            ["url"] = site.Url,
            ["vendor"] = site.Vendor ?? "other",
            ["_root"] = "/",
        };
        if (site.User.Length > 0) fs["user"] = site.User;
        if (site.SealedPassword is not null)
            fs["pass"] = await client.ObscureAsync(Open(site.SealedPassword, "password"), cancellationToken).ConfigureAwait(false);
        return fs;
    }

    /// <summary>
    /// S3: buckets are the top folders. The secret access key is not one of rclone's obscured options, so it is passed as
    /// it is and registered as a secret (no error text carries it). Credentials come only from the site, never from this
    /// PC's environment.
    /// </summary>
    private static JsonObject S3(SiteRecord site, RcloneClient client)
    {
        var secret = site.SealedPassword is null
            ? throw new SiteStoreException("No secret access key is saved for this site; type it in Edit site.")
            : Open(site.SealedPassword, "secret access key");
        client.AddSecret(secret);
        var fs = new JsonObject
        {
            ["type"] = "s3",
            ["provider"] = site.Provider ?? "Other",
            ["env_auth"] = "false",
            ["access_key_id"] = site.User,
            ["secret_access_key"] = secret,
            ["_root"] = "/",
        };
        if (!string.IsNullOrEmpty(site.Endpoint)) fs["endpoint"] = site.Endpoint;
        if (!string.IsNullOrEmpty(site.Region)) fs["region"] = site.Region;
        return fs;
    }

    private static string Open(string? sealedValue, string what) =>
        SecretProtector.UnprotectSiteSecret(sealedValue)
        ?? throw new SiteStoreException($"The site's {what} can't be opened on this PC; enter it again in Edit site.");
}
