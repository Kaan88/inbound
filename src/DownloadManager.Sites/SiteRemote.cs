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
        SiteKinds.B2 => B2(site, client),
        SiteKinds.Drive or SiteKinds.OneDrive or SiteKinds.Dropbox => await SignedInAsync(site, client, cancellationToken).ConfigureAwait(false),
        _ => throw new SiteStoreException($"Remotes of kind \"{site.Kind}\" are not supported."),
    };

    private static async Task<JsonObject> SftpAsync(SiteRecord site, RcloneClient client, CancellationToken cancellationToken)
    {
        // rclone does not check an SFTP host key unless told to (SITES.0 point 3), so a site without an accepted key never
        // reaches it: the window asks first.
        if (site.NeedsHostKey) throw new SiteStoreException("The server's key has not been accepted yet.");
        // SITES.11 review: an address opened once with no password would make rclone offer this PC's ssh-agent keys to
        // whatever server was typed. Here, so browsing, transfers, folder changes and device streams all stop.
        if (site is { Unsaved: true, AuthMethod: SiteAuthMethods.Password, SealedPassword: null })
            throw new SiteStoreException("Type the password for this server.");

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
            if (site.SealedPrivateKey is null) throw new SiteStoreException("No private key is saved for this remote; choose one in Edit remote.");
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
            ? throw new SiteStoreException("No secret access key is saved for this remote; type it in Edit remote.")
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

    /// <summary>SITES.6c, Backblaze B2: the application key is not one of rclone's obscured options, so it is registered as a secret.</summary>
    private static JsonObject B2(SiteRecord site, RcloneClient client)
    {
        var key = site.SealedPassword is null
            ? throw new SiteStoreException("No application key is saved for this remote; type it in Edit remote.")
            : Open(site.SealedPassword, "application key");
        client.AddSecret(key);
        return new JsonObject { ["type"] = "b2", ["account"] = site.User, ["key"] = key, ["_root"] = "/" };
    }

    /// <summary>The name of a sign-in site's in-memory remote in rclone (never written to a config file).</summary>
    public static string RemoteName(SiteRecord site) => "site-" + site.Id;

    /// <summary>
    /// Uses an operation-scoped remote so another session cannot reconfigure an upload's destination
    /// while its body is in flight. The caller is responsible for confirming endpoint compatibility.
    /// </summary>
    public static async Task CreateS3FileAsync(SiteRecord site, RcloneClient client, string folder, string name,
        Stream content, CancellationToken cancellationToken)
    {
        if (site.Kind != SiteKinds.S3 || folder is "" or "/" || SitePaths.Normalize(folder) != folder
            || SitePaths.NameProblem(name) is not null || name.Contains('\\'))
            throw new ArgumentException("An S3 bucket and valid object name are required.");
        var isolated = site with { Id = Guid.NewGuid().ToString("N") };
        var remote = RemoteName(isolated);
        var fs = S3(isolated, client);
        fs.Remove("type");
        fs.Remove("_root");
        fs["no_check_bucket"] = "true"; // Never create a bucket as a side effect of a confirmed object upload.
        try
        {
            await client.CallAsync("config/create", new JsonObject
            {
                ["name"] = remote, ["type"] = "s3", ["parameters"] = fs,
                ["opt"] = new JsonObject { ["nonInteractive"] = true, ["noObscure"] = true }
            }, cancellationToken).ConfigureAwait(false);
            await client.CreateS3FileAsync(remote, folder, name, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await client.CallAsync("config/delete", new JsonObject { ["name"] = remote }, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException or OperationCanceledException) { }
        }
    }

    /// <summary>
    /// SITES.7: the site as a named in-memory remote (<c>config/create</c>), which rclone's file serving and
    /// <c>operations/uploadfile</c> need. A sign-in site already is one; another kind is made (again) from its fs object,
    /// whose secrets are already obscured or registered. Returns the remote's name.
    /// </summary>
    public static async Task<string> NamedAsync(SiteRecord site, RcloneClient client, CancellationToken cancellationToken = default)
    {
        var fs = await BuildAsync(site, client, cancellationToken).ConfigureAwait(false);
        if (fs["_name"]?.GetValue<string>() is { } named) return named;
        var name = RemoteName(site);
        var parameters = new JsonObject();
        foreach (var (key, value) in fs)
            if (key is not ("type" or "_root")) parameters[key] = value?.DeepClone();
        await client.CallAsync("config/create", new JsonObject
        {
            ["name"] = name,
            ["type"] = fs["type"]!.GetValue<string>(),
            ["parameters"] = parameters,
            ["opt"] = new JsonObject { ["nonInteractive"] = true, ["noObscure"] = true },
        }, cancellationToken).ConfigureAwait(false);
        return name;
    }

    /// <summary>
    /// SITES.6c: Drive, OneDrive and Dropbox. Each is a named in-memory remote (<c>config/create</c> with
    /// <c>--config ""</c>), because rclone writes a renewed token back only to a named remote; an <c>fs</c> object with
    /// <c>_name</c> uses it. Before it is used again the token rclone holds is compared with the one it was given: a renewed
    /// one is handed to <see cref="RcloneClient.SignInRenewed"/> to be sealed and stored.
    /// </summary>
    private static async Task<JsonObject> SignedInAsync(SiteRecord site, RcloneClient client, CancellationToken cancellationToken)
    {
        if (site.SealedSignIn is null) throw new SiteSignInNeededException("Sign in to this remote first (Options > Remote files).");
        var stored = ParseSignIn(Open(site.SealedSignIn, "sign-in"), client);
        var storedToken = stored["token"]?.GetValue<string>() ?? throw new SiteSignInNeededException("The remote's sign-in is incomplete; sign in again.");
        var name = RemoteName(site);

        if (client.SignedInRemotes.TryGetValue(name, out var given))
        {
            var held = await client.CallAsync("config/get", new JsonObject { ["name"] = name }, cancellationToken).ConfigureAwait(false);
            var heldToken = held["token"]?.GetValue<string>();
            if (heldToken is not null) RegisterToken(heldToken, client);
            if (heldToken is not null && heldToken != given && storedToken == given)
            {
                // rclone renewed the token since it was given: that one is kept, here and in the site.
                client.SignedInRemotes[name] = heldToken;
                stored["token"] = heldToken;
                if (client.SignInRenewed is { } renewed) await renewed(site.Id, given, stored, cancellationToken).ConfigureAwait(false);
                return Named(name);
            }

            if (storedToken == given && heldToken == given) return Named(name);
        }

        // New to this rclone, or signed in again since: the remote is made from the stored settings.
        var parameters = (JsonObject)stored.DeepClone();
        if (site.ClientId is { } clientId)
        {
            parameters["client_id"] = clientId;
            if (site.SealedClientSecret is not null)
            {
                var secret = Open(site.SealedClientSecret, "client secret");
                client.AddSecret(secret);
                parameters["client_secret"] = secret;
            }
        }

        await client.CallAsync("config/create", new JsonObject
        {
            ["name"] = name,
            ["type"] = site.Kind,
            ["parameters"] = parameters,
            // Values are given as they are, and rclone asks nothing: it would otherwise start its own setup questions.
            ["opt"] = new JsonObject { ["nonInteractive"] = true, ["noObscure"] = true },
        }, cancellationToken).ConfigureAwait(false);
        client.SignedInRemotes[name] = storedToken;
        return Named(name);
    }

    private static JsonObject Named(string name) => new() { ["_name"] = name, ["_root"] = "/" };

    /// <summary>The sealed settings of a sign-in site as a JSON object, with every token in them registered as a secret.</summary>
    public static JsonObject ParseSignIn(string json, RcloneClient client)
    {
        client.AddSecret(json);
        JsonObject parsed;
        try
        {
            parsed = JsonNode.Parse(json) as JsonObject ?? throw new SiteSignInNeededException("The remote's sign-in can't be read; sign in again.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new SiteSignInNeededException("The remote's sign-in can't be read; sign in again.");
        }

        if (parsed["token"]?.GetValue<string>() is { } token) RegisterToken(token, client);
        return parsed;
    }

    /// <summary>The token JSON, its access and refresh tokens, and the JSON-escaped form rclone may print, as secrets.</summary>
    public static void RegisterToken(string token, RcloneClient client)
    {
        client.AddSecret(token);
        client.AddSecret(System.Text.Json.JsonSerializer.Serialize(token, SiteRemoteJsonContext.Default.String)[1..^1]);
        try
        {
            if (JsonNode.Parse(token) is JsonObject fields)
            {
                foreach (var field in new[] { "access_token", "refresh_token" })
                    if (fields[field]?.GetValue<string>() is { Length: > 0 } value) client.AddSecret(value);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: the whole text is already registered.
        }
    }

    private static string Open(string? sealedValue, string what) =>
        SecretProtector.UnprotectSiteSecret(sealedValue)
        ?? throw new SiteStoreException($"The remote's {what} can't be opened on this PC; enter it again in Edit remote.");
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class SiteRemoteJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
