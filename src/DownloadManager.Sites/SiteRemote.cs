using System.Text.Json.Nodes;
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
    public static async Task<JsonObject> BuildAsync(SiteRecord site, RcloneClient client, CancellationToken cancellationToken = default)
    {
        if (site.Kind != SiteKinds.Sftp) throw new SiteStoreException($"Sites of kind \"{site.Kind}\" are not supported yet.");
        // rclone does not check an SFTP host key unless told to (SITES.0 point 3), so a site without an accepted key never
        // reaches it: the window asks first.
        if (site.HostKeys.Count == 0) throw new SiteStoreException("The server's key has not been accepted yet.");

        var fs = new JsonObject
        {
            ["type"] = "sftp",
            ["host"] = site.Host,
            ["port"] = site.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

    private static string Open(string? sealedValue, string what) =>
        SecretProtector.UnprotectSiteSecret(sealedValue)
        ?? throw new SiteStoreException($"The site's {what} can't be opened on this PC; enter it again in Edit site.");
}
