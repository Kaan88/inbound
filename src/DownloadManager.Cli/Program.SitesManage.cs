using DownloadManager.Contracts;

namespace DownloadManager.Cli;

/// <summary>
/// SITES.8: the rest of <c>dm sites</c> (remote-sites.md, "Clients"): the switch, adding and removing sites, a test, the
/// questions a site asks before first use (a server key, a certificate, a browser sign-in), and the site's own files
/// (<c>ls</c>, <c>mkdir</c>, <c>mv</c>, <c>rm</c>). A password, key passphrase or client secret is never an argument: it is
/// read from standard input (not shown at a console) or from an environment variable named with <c>-env</c>.
/// </summary>
internal static partial class Program
{
    internal const string SitesManageUsage = """
               sites on|off|status
               sites rclone [<version>]   (downloads, checks and installs rclone, as Options › Remote files › Download rclone)
               sites add <name> --kind sftp|ftp|webdav|s3|drive|onedrive|dropbox|b2 [--host H] [--port N] [--user U]
                         [--key-file F] [--url U] [--vendor V] [--provider P] [--endpoint E] [--region R] [--tls T]
                         [--client-id ID] [--folder /start] [--local-folder D] [--password-env VAR]
                         [--key-passphrase-env VAR] [--client-secret-env VAR]   (otherwise a password is read from standard input)
               sites remove <remote>
               sites test <remote>
               sites trust <remote> [--fingerprint SHA256:...]   (a server key or certificate; asks at a console)
               sites sign-in <remote> [--token-stdin]   (Drive, OneDrive, Dropbox: prints the address, waits)
               sites ls <remote> [<path>]
               sites mkdir <remote> <path>
               sites mv <remote> <path> <new name>
               sites rm <remote> <path>... --yes   (deletes on the server; folders with everything in them)
        """;

    /// <summary>One of the commands here, or null when <paramref name="sub"/> is not one of them.</summary>
    private static async Task<int?> SitesManageAsync(CliClient client, string sub, string[] args, string[] positional, bool json)
    {
        switch (sub)
        {
            case "on" or "off":
            {
                var status = await client.AskAsync<SitesStatusDto>(SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = sub == "on" })
                    .ConfigureAwait(false);
                return PrintSitesStatus(status, json);
            }
            case "status":
                return PrintSitesStatus(await client.AskAsync<SitesStatusDto>(SitesMessageTypes.Get, new EmptyPayload()).ConfigureAwait(false), json);
            case "rclone":
            {
                // MAC.1: a headless install (and the macOS workflow) has no Options window to press Download rclone in.
                var status = await client.AskAsync<SitesStatusDto>(SitesMessageTypes.RcloneInstall,
                    new SitesRcloneInstallRequest { Version = positional.ElementAtOrDefault(1) }).ConfigureAwait(false);
                PrintSitesStatus(status, json);
                return status.Error is { Length: > 0 } ? 1 : 0;
            }
            case "add":
                return await AddSiteAsync(client, args, positional, json).ConfigureAwait(false);
            case "remove":
            {
                if (await FindSiteAsync(client, positional.ElementAtOrDefault(1)).ConfigureAwait(false) is not { } site) return 1;
                return PrintSiteResult(await client.AskAsync<SiteResultDto>(SitesMessageTypes.Remove, new SiteIdRequest { Id = site.Id }).ConfigureAwait(false),
                    json, $"Removed {site.Name}. Nothing on the server was deleted.");
            }
            case "test":
            {
                if (await FindSiteAsync(client, positional.ElementAtOrDefault(1)).ConfigureAwait(false) is not { } site) return 1;
                return PrintSiteResult(await client.AskAsync<SiteResultDto>(SitesMessageTypes.Test, new SiteIdRequest { Id = site.Id }).ConfigureAwait(false),
                    json, $"{site.Name} answered.");
            }
            case "trust":
                return await TrustSiteAsync(client, args, positional, json).ConfigureAwait(false);
            case "sign-in" or "signin":
                return await SignInSiteAsync(client, args, positional, json).ConfigureAwait(false);
            case "ls":
                return await ListSiteFolderAsync(client, positional, json).ConfigureAwait(false);
            case "mkdir" or "mv" or "rm":
                return await ChangeSiteAsync(client, sub, args, positional, json).ConfigureAwait(false);
            default:
                return null;
        }
    }

    private static int PrintSitesStatus(SitesStatusDto status, bool json)
    {
        if (json) { PrintJson(status); return 0; }
        Console.WriteLine($"Remote files: {(status.Enabled ? "on" : "off")}");
        var rclone = status.Rclone + (status.RcloneVersion is { Length: > 0 } version ? $" ({version})" : "");
        if (status.RcloneInstalling) rclone = "downloading";
        Console.WriteLine($"rclone: {rclone}");
        if (status.Error is { Length: > 0 } error) Console.WriteLine($"problem: {error}");
        return 0;
    }

    private static int PrintSiteResult(SiteResultDto result, bool json, string done)
    {
        if (json) { PrintJson(result); return result.Ok ? 0 : 1; }
        if (result.Ok)
        {
            Console.WriteLine(done);
            return 0;
        }

        Console.Error.WriteLine($"failed: {result.Message}");
        if (SiteProblemHint(result.Problem) is { } hint) Console.Error.WriteLine(hint);
        return 1;
    }

    private static string? SiteProblemHint(string? problem) => problem switch
    {
        SiteProblems.HostKeyNotAccepted or SiteProblems.HostKeyChanged or SiteProblems.CertificateNotTrusted =>
            "Check the server's key or certificate with 'sites trust <remote>'.",
        SiteProblems.SignInNeeded => "Sign in with 'sites sign-in <remote>'.",
        SiteProblems.Off => "Turn Remote files on with 'sites on'.",
        _ => null,
    };

    /// <summary>A saved site by name (any case) or the start of its id; null after saying why.</summary>
    private static async Task<SiteDto?> FindSiteAsync(CliClient client, string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted))
        {
            Console.Error.WriteLine("Name a saved remote; see 'sites list'.");
            return null;
        }

        var sites = await client.AskAsync<SitesListDto>(SitesMessageTypes.ListRequest, new EmptyPayload()).ConfigureAwait(false);
        var found = sites.Sites.Where(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0) found = [.. sites.Sites.Where(s => s.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))];
        if (found.Count == 1) return found[0];
        Console.Error.WriteLine($"'{wanted}' did not match exactly one saved remote; see 'sites list'.");
        return null;
    }

    /// <summary>A path on a site as the Host takes it: absolute, with "/".</summary>
    internal static string SitePath(string? path) => "/" + (path ?? "").Replace('\\', '/').Trim('/');

    private static (string Folder, string Name) SplitSitePath(string path)
    {
        var at = path.LastIndexOf('/');
        return (at <= 0 ? "/" : path[..at], path[(at + 1)..]);
    }

    private static string? SecretFrom(string[] args, string envFlag, string prompt, bool ask)
    {
        if (GetOption(args, envFlag) is { } name) return Environment.GetEnvironmentVariable(name);
        return ask ? SecretInput.ReadLine(prompt) : null;
    }

    private static async Task<int> AddSiteAsync(CliClient client, string[] args, string[] positional, bool json)
    {
        var name = positional.ElementAtOrDefault(1);
        var kindName = GetOption(args, "--kind") ?? SiteKindCatalog.Sftp;
        if (string.IsNullOrWhiteSpace(name) || SiteKindCatalog.Find(kindName) is not { } kind)
        {
            Console.Error.WriteLine("usage: " + SitesManageUsage.TrimStart().Split('\n')[1].Trim());
            Console.Error.WriteLine($"--kind is one of: {string.Join(", ", SiteKindCatalog.All.Select(k => k.Kind))}.");
            return 2;
        }

        foreach (var flag in new[] { "--password", "--key-passphrase", "--client-secret" })
        {
            if (GetOption(args, flag) is not null)
            {
                Console.Error.WriteLine($"A secret is only read from standard input or {flag}-env, so {flag} was not used. What you just typed may be in your shell history.");
                return 2;
            }
        }

        var has = kind.Fields.ToHashSet(StringComparer.Ordinal);
        var keyFile = GetOption(args, "--key-file");
        var request = new SiteSaveRequest
        {
            Name = name.Trim(),
            Kind = kind.Kind,
            Host = GetOption(args, "--host") ?? "",
            User = GetOption(args, "--user") ?? "",
            RemoteFolder = GetOption(args, "--folder") ?? "",
            LocalFolder = GetOption(args, "--local-folder") is { } local ? Path.GetFullPath(local) : null,
            Url = GetOption(args, "--url"),
            Vendor = GetOption(args, "--vendor") ?? (has.Contains(SiteFields.Vendor) ? kind.Choices[SiteFields.Vendor][0].Value : null),
            Provider = GetOption(args, "--provider") ?? (has.Contains(SiteFields.Provider) ? kind.Choices[SiteFields.Provider][0].Value : null),
            Endpoint = GetOption(args, "--endpoint"),
            Region = GetOption(args, "--region"),
            Tls = GetOption(args, "--tls") ?? (has.Contains(SiteFields.Tls) ? kind.Choices[SiteFields.Tls][0].Value : null),
            ClientId = GetOption(args, "--client-id"),
            AuthMethod = keyFile is not null ? "key" : kind.AuthMethods.FirstOrDefault() ?? "password",
        };
        if (GetOption(args, "--port") is { } portText)
        {
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            {
                Console.Error.WriteLine("--port takes a number from 1 to 65535.");
                return 2;
            }

            request.Port = port;
        }
        else request.Port = kind.DefaultPort;

        if (keyFile is not null)
        {
            if (!has.Contains(SiteFields.Key))
            {
                Console.Error.WriteLine($"A {kind.Title} remote doesn't sign in with a key file.");
                return 2;
            }

            try
            {
                request.PrivateKey = await File.ReadAllTextAsync(keyFile).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The key file could not be read: {e.Message}");
                return 2;
            }

            request.KeyPassphrase = SecretFrom(args, "--key-passphrase-env", "", ask: false) is { Length: > 0 } passphrase ? passphrase : null;
        }
        else if (has.Contains(SiteFields.Password))
        {
            request.Password = SecretFrom(args, "--password-env", $"{kind.PasswordLabel ?? "Password"}: ", ask: true);
            if (string.IsNullOrEmpty(request.Password))
            {
                Console.Error.WriteLine("No password was given. Pipe it in, type it, or use --password-env VAR.");
                return 2;
            }
        }

        if (has.Contains(SiteFields.SignIn))
            request.ClientSecret = SecretFrom(args, "--client-secret-env", "", ask: false) is { Length: > 0 } secret ? secret : null;

        var saved = await client.AskAsync<SiteResultDto>(SitesMessageTypes.Save, request).ConfigureAwait(false);
        if (json) { PrintJson(saved); return saved.Ok ? 0 : 1; }
        if (!saved.Ok || saved.Site is not { } site)
        {
            Console.Error.WriteLine($"failed: {saved.Message}");
            return 1;
        }

        Console.WriteLine($"Saved {site.Name} ({site.Id[..8]}).");
        if (!site.Encrypted) Console.WriteLine("Not encrypted: the password and every file cross the network as they are.");
        if (kind.Capabilities.Contains(SiteCapabilities.HostKey))
            Console.WriteLine($"Before first use, check and trust the server's key: dm sites trust \"{site.Name}\"");
        else if (kind.Capabilities.Contains(SiteCapabilities.SignIn))
            Console.WriteLine($"Before first use, sign in: dm sites sign-in \"{site.Name}\"");
        return 0;
    }

    private static async Task<int> TrustSiteAsync(CliClient client, string[] args, string[] positional, bool json)
    {
        if (await FindSiteAsync(client, positional.ElementAtOrDefault(1)).ConfigureAwait(false) is not { } site) return 1;
        var wanted = GetOption(args, "--fingerprint");
        if (site.Kind == SiteKindCatalog.Sftp)
        {
            var probe = await client.AskAsync<SiteProbeResultDto>(SitesMessageTypes.Probe, new SiteProbeRequest { Host = site.Host, Port = site.Port })
                .ConfigureAwait(false);
            if (!probe.Ok || probe.Keys.Count == 0)
            {
                Console.Error.WriteLine($"failed: {probe.Message ?? "The server showed no key."}");
                return 1;
            }

            var trusted = site.HostKeys.Select(k => k.Entry).ToHashSet(StringComparer.Ordinal);
            if (!json)
            {
                Console.WriteLine($"{site.Host}:{site.Port} shows {(probe.Keys.Count == 1 ? "this key" : "these keys")}:");
                foreach (var key in probe.Keys)
                    Console.WriteLine($"  {key.Type,-20} {key.Fingerprint}{(trusted.Contains(key.Entry) ? "  (trusted)" : "")}");
                if (trusted.Count > 0 && !probe.Keys.Any(k => trusted.Contains(k.Entry)))
                    Console.WriteLine("None of them is the key you trusted before. If nobody changed the server, someone may be between you and it.");
            }

            List<SiteHostKeyDto> accept;
            if (wanted is not null)
            {
                accept = [.. probe.Keys.Where(k => string.Equals(k.Fingerprint, wanted.Trim(), StringComparison.Ordinal))];
                if (accept.Count == 0)
                {
                    Console.Error.WriteLine("The server shows no key with that fingerprint; nothing was trusted.");
                    return 1;
                }
            }
            else if (!Confirm("Trust the server's keys? Check them with the server's owner first. [y/N] "))
            {
                Console.Error.WriteLine("Nothing was trusted. To trust without a question, give --fingerprint.");
                return 1;
            }
            else accept = probe.Keys;

            return PrintSiteResult(await client.AskAsync<SiteResultDto>(SitesMessageTypes.Trust,
                new SiteTrustRequest { Id = site.Id, Entries = [.. accept.Select(k => k.Entry)] }).ConfigureAwait(false), json, $"Trusted; {site.Name} can be used.");
        }

        var read = await client.AskAsync<SiteCertificateResultDto>(SitesMessageTypes.Certificate, new SiteIdRequest { Id = site.Id }).ConfigureAwait(false);
        if (!read.Ok || read.Certificate is not { } certificate)
        {
            Console.Error.WriteLine($"failed: {read.Message ?? "The certificate could not be read."}");
            return 1;
        }

        if (!json)
        {
            Console.WriteLine($"Issued to:   {certificate.Subject}");
            Console.WriteLine($"Issued by:   {(certificate.SelfSigned ? "itself (self-signed)" : certificate.Issuer)}");
            Console.WriteLine($"Valid until: {certificate.NotAfter:yyyy-MM-dd}");
            Console.WriteLine($"Fingerprint: {certificate.Fingerprint}");
        }

        if (certificate.TrustedBySystem)
        {
            Console.WriteLine("Windows already trusts this certificate; nothing to do.");
            return 0;
        }

        if (certificate.CannotTrust is { Length: > 0 } cannot)
        {
            Console.Error.WriteLine($"failed: {cannot}");
            return 1;
        }

        if (wanted is not null && !string.Equals(wanted.Trim(), certificate.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The server's certificate has another fingerprint; nothing was trusted.");
            return 1;
        }

        if (wanted is null && !Confirm("Trust this certificate for this remote? [y/N] "))
        {
            Console.Error.WriteLine("Nothing was trusted. To trust without a question, give --fingerprint.");
            return 1;
        }

        return PrintSiteResult(await client.AskAsync<SiteResultDto>(SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = site.Id, CertificateFingerprint = certificate.Fingerprint }).ConfigureAwait(false), json, $"Trusted; {site.Name} can be used.");
    }

    /// <summary>A yes at a console; never at redirected input, where nobody saw the question.</summary>
    private static bool Confirm(string question)
    {
        if (Console.IsInputRedirected) return false;
        Console.Write(question);
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static async Task<int> SignInSiteAsync(CliClient client, string[] args, string[] positional, bool json)
    {
        if (await FindSiteAsync(client, positional.ElementAtOrDefault(1)).ConfigureAwait(false) is not { } site) return 1;
        if (CliArgs.HasFlag(args, "--token-stdin"))
        {
            var token = Console.IsInputRedirected ? await Console.In.ReadToEndAsync().ConfigureAwait(false) : SecretInput.ReadLine("Token: ");
            var pasted = await client.AskAsync<SiteSignInResultDto>(SitesMessageTypes.SignIn, new SiteSignInRequest { Id = site.Id, Token = token })
                .ConfigureAwait(false);
            if (json) { PrintJson(pasted); return pasted.Ok ? 0 : 1; }
            if (pasted.Ok) { Console.WriteLine($"Signed in; {site.Name} can be used."); return 0; }
            Console.Error.WriteLine($"failed: {pasted.Message}");
            return 1;
        }

        var started = await client.AskAsync<SiteSignInResultDto>(SitesMessageTypes.SignIn, new SiteSignInRequest { Id = site.Id }).ConfigureAwait(false);
        if (json) PrintJson(started);
        if (started.Url is not { Length: > 0 } url)
        {
            if (!json) Console.Error.WriteLine($"failed: {started.Message}");
            if (started.AuthorizeCommand is { Length: > 0 } command && !json)
                Console.Error.WriteLine($"On a computer with a browser, run: {command}\nthen: dm sites sign-in \"{site.Name}\" --token-stdin");
            return 1;
        }

        if (!json)
        {
            Console.WriteLine($"Open this address in a browser on the computer Inbound runs on, and allow access:\n  {url}");
            if (started.AuthorizeCommand is { Length: > 0 } command)
                Console.WriteLine($"From another computer: run {command} there, then dm sites sign-in \"{site.Name}\" --token-stdin");
            Console.WriteLine("Waiting (Ctrl+C stops waiting; the sign-in ends by itself after 10 minutes)…");
        }

        var until = DateTime.UtcNow.AddMinutes(11);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var now = (await client.AskAsync<SitesListDto>(SitesMessageTypes.ListRequest, new EmptyPayload()).ConfigureAwait(false))
                .Sites.FirstOrDefault(s => s.Id == site.Id);
            if (now is null) { Console.Error.WriteLine("The remote was removed."); return 1; }
            if (now.SigningIn) continue;
            if (now.SignedIn) { if (!json) Console.WriteLine($"Signed in; {site.Name} can be used."); return 0; }
            Console.Error.WriteLine("The sign-in ended without access.");
            return 1;
        }

        Console.Error.WriteLine("The sign-in took too long.");
        return 1;
    }

    private static async Task<int> ListSiteFolderAsync(CliClient client, string[] positional, bool json)
    {
        if (await FindSiteAsync(client, positional.ElementAtOrDefault(1)).ConfigureAwait(false) is not { } site) return 1;
        var path = positional.Length > 2 ? SitePath(positional[2]) : site.RemoteFolder is { Length: > 0 } start ? start : "/";
        var items = new List<SiteItemDto>();
        string? listing = null;
        SiteBrowseResultDto page;
        do
        {
            page = await client.AskAsync<SiteBrowseResultDto>(SitesMessageTypes.Browse,
                new SiteBrowseRequest { Id = site.Id, Path = path, Offset = items.Count, Limit = 500, Listing = listing, Refresh = listing is null }).ConfigureAwait(false);
            if (!page.Ok)
            {
                if (json) { PrintJson(page); return 1; }
                Console.Error.WriteLine($"failed: {page.Message}");
                if (SiteProblemHint(page.Problem) is { } hint) Console.Error.WriteLine(hint);
                return 1;
            }

            items.AddRange(page.Items);
            listing = page.Listing;
        }
        while (items.Count < page.Total && page.Items.Count > 0);

        if (json) { PrintJson(new { path = page.Path, items }); return 0; }
        Console.WriteLine($"{site.Name}:{page.Path}");
        foreach (var item in items)
        {
            var size = item.IsFolder ? "<folder>" : item.Size >= 0 ? item.Size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "";
            var modified = item.Modified is { } when ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "";
            Console.WriteLine($"{size,16}  {modified,-16}  {item.Name}{(item.IsFolder ? "/" : "")}");
        }

        Console.WriteLine($"{items.Count} item{(items.Count == 1 ? "" : "s")}");
        return 0;
    }

    private static async Task<int> ChangeSiteAsync(CliClient client, string sub, string[] args, string[] positional, bool json)
    {
        var minimum = sub switch { "mv" => 4, _ => 3 };
        if (positional.Length < minimum)
        {
            Console.Error.WriteLine(SitesManageUsage);
            return 2;
        }

        if (await FindSiteAsync(client, positional[1]).ConfigureAwait(false) is not { } site) return 1;
        switch (sub)
        {
            case "mkdir":
            {
                var (folder, name) = SplitSitePath(SitePath(positional[2]));
                return PrintSiteResult(await client.AskAsync<SiteResultDto>(SitesMessageTypes.MakeFolder,
                    new SiteMakeFolderRequest { Id = site.Id, Path = folder, Name = name }).ConfigureAwait(false), json, $"Made {SitePath(positional[2])}.");
            }
            case "mv":
            {
                var (folder, name) = SplitSitePath(SitePath(positional[2]));
                var newName = positional[3];
                if (newName.Contains('/') || newName.Contains('\\'))
                {
                    Console.Error.WriteLine("mv renames in the same folder: give the new name alone.");
                    return 2;
                }

                return PrintSiteResult(await client.AskAsync<SiteResultDto>(SitesMessageTypes.Rename,
                    new SiteRenameRequest { Id = site.Id, Path = folder, Name = name, NewName = newName }).ConfigureAwait(false), json, $"Renamed to {newName}.");
            }
            default:
            {
                var paths = positional.Skip(2).Select(SitePath).ToList();
                var folders = paths.Select(p => SplitSitePath(p).Folder).Distinct(StringComparer.Ordinal).ToList();
                if (folders.Count != 1 || paths.Contains("/"))
                {
                    Console.Error.WriteLine("The paths of one 'sites rm' must be in one folder on the remote.");
                    return 2;
                }

                var request = new SiteChangeRequest { Id = site.Id, Path = folders[0], Items = [] };
                foreach (var path in paths) request.Items.Add(new SiteChangeItemDto { Name = SplitSitePath(path).Name });
                // What is there decides file or folder, and how much goes: told before anything is deleted.
                var count = await client.AskAsync<SiteCountResultDto>(SitesMessageTypes.Count, request).ConfigureAwait(false);
                if (!count.Ok)
                {
                    Console.Error.WriteLine($"failed: {count.Message}");
                    return 1;
                }

                if (!CliArgs.HasFlag(args, "--yes"))
                {
                    Console.Error.WriteLine($"This deletes {count.Files:N0} file(s) in {count.Folders:N0} folder(s) on {site.Name}, and it can't be undone. Add --yes to do it.");
                    return 2;
                }

                var deleted = await client.AskAsync<SiteChangeResultDto>(SitesMessageTypes.Delete, request).ConfigureAwait(false);
                if (json) { PrintJson(deleted); return deleted.Ok ? 0 : 1; }
                foreach (var item in deleted.Items.Where(i => !i.Ok)) Console.Error.WriteLine($"{item.Name}: {item.Message}");
                if (!deleted.Ok)
                {
                    Console.Error.WriteLine($"failed: {deleted.Message ?? "not everything was deleted."}");
                    return 1;
                }

                Console.WriteLine($"Deleted {deleted.Items.Count(i => i.Ok)} item(s).");
                return 0;
            }
        }
    }
}
