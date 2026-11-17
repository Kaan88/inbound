using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The flows on one remote that need the user: save (Add a remote / Edit remote), remove, test with its host-key and
/// certificate questions, and the browser sign-in. Shared by Options > Sites and the All remotes page, which differ only in
/// where their result line goes and how they mark themselves busy. A dialog here is always opened on <c>XamlRoot</c> of the window.
/// </summary>
internal sealed partial class SiteFlows
{
    private readonly RemoteClient _client;
    private readonly Window _window;
    private readonly CancellationToken _closing;
    private readonly Action<string, bool> _say;
    private readonly Func<Func<Task>, Task> _run;
    private readonly Action _render;
    private IReadOnlyList<SiteKindDto> _kinds = [];

    public SiteFlows(RemoteClient client, Window window, CancellationToken closing, Action<string, bool> say, Func<Func<Task>, Task> run, Action render)
    {
        _client = client;
        _window = window;
        _closing = closing;
        _say = say;
        _run = run;
        _render = render;
    }

    /// <summary>The remotes the owner shows; the flows keep it current after a save, a trust or a sign-in.</summary>
    public List<SiteDto> Sites { get; set; } = [];

    public IReadOnlyList<SiteKindDto> Kinds
    {
        get => _kinds;
        set => _kinds = value;
    }

    private void Say(string text, bool isProblem = false) => _say(text, isProblem);

    private void Render() => _render();

    private Task RunAsync(Func<Task> action) => _run(action);

    private bool IsClosing => _closing.IsCancellationRequested;

    public async Task SaveFormAsync(SiteDto? existing)
    {
        // An address opened once lives in the Host's memory only: it is remembered first, then edited.
        if (existing is not null && !SitesHomeRules.CanEdit(existing))
        {
            Say($"{existing.Name} was opened once and isn't saved. Remember it first, then edit it.", isProblem: true);
            return;
        }

        SiteDto? saved;
        try
        {
            if (_kinds.Count == 0 && (await _client.GetSiteKindsAsync(_closing)).Value is { } known) _kinds = known.Kinds;
            saved = await SiteDialog.ShowAsync(_window, _kinds, existing, new SiteDialogCalls(
                (request, cancel) => Linked(cancel, token => _client.SaveSiteAsync(request, token)),
                (id, cancel) => Linked(cancel, token => _client.TestSiteAsync(id, token)),
                (request, keys, cancel) => Linked(cancel, token => _client.TestSiteDraftAsync(request, keys, token)),
                (id, keys, cancel) => Linked(cancel, token => _client.TrustSiteAsync(id, keys, token)),
                (id, fingerprint, cancel) => Linked(cancel, token => _client.TrustSiteCertificateAsync(id, fingerprint, token)),
                (id, cancel) => Linked(cancel, token => _client.RemoveSiteAsync(id, token))));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (saved is null) return;
        var index = Sites.FindIndex(s => s.Id == saved.Id);
        if (index >= 0) Sites[index] = saved;
        else Sites.Add(saved);
        Render();
        Say($"{saved.Name} is saved.", false);

        // A new server, or a changed host or port, has no trusted key yet: ask now, then try the site. A new site of any
        // kind is tried at once, so a wrong address or key is found while it is fresh.
        // A site that signs in through the browser does that first (and again after a new client ID); the test follows.
        if (SiteKindCatalog.SignsIn(saved.Kind) && !saved.SignedIn) await SignInAsync(saved);
        else if (SitesRules.NeedsTrust(saved) || existing is null) await TestSiteAsync(saved);
    }

    private async Task<T> Linked<T>(CancellationToken cancel, Func<CancellationToken, Task<T>> call)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_closing, cancel);
        return await call(linked.Token);
    }

    /// <summary>"Remember": an address opened once is saved (<c>sites.keep</c>) and becomes an ordinary remote.</summary>
    public async Task RememberAsync(SiteDto site)
    {
        await RunAsync(async () =>
        {
            var result = await _client.KeepSiteAsync(site.Id, _closing);
            if (!result.Ok)
            {
                Say(string.IsNullOrWhiteSpace(result.Message) ? $"{site.Name} couldn't be remembered." : result.Message, isProblem: true);
                return;
            }

            if (result.Site is { } kept)
            {
                var index = Sites.FindIndex(s => s.Id == kept.Id);
                if (index >= 0) Sites[index] = kept;
                else Sites.Add(kept);
                Render();
            }

            Say($"{site.Name} is remembered: it is a saved remote now.");
        });
    }

    public async Task RemoveAsync(SiteDto site)
    {
        var body = new TextBlock
        {
            Text = $"Remove {site.Name}? Its saved password or key and the key you trusted are deleted from this PC. Nothing on the server is touched.",
            TextWrapping = TextWrapping.Wrap,
        };
        if (await AskAsync($"Remove {site.Name}?", body, "Remove", "Cancel", danger: true, defaultClose: true) != ContentDialogResult.Primary) return;
        await RunAsync(async () =>
        {
            var result = await _client.RemoveSiteAsync(site.Id, _closing);
            if (!result.Ok)
            {
                Say(string.IsNullOrWhiteSpace(result.Message) ? $"{site.Name} couldn't be removed." : result.Message, isProblem: true);
                return;
            }

            Sites.RemoveAll(s => s.Id == site.Id);
            Say($"{site.Name} is removed.");
        });
    }

    public async Task TestSiteAsync(SiteDto site, bool afterSignIn = false)
    {
        // Twice at most: once, and again after the key was trusted or replaced.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            SiteResultDto? result = null;
            await RunAsync(async () =>
            {
                Say($"Testing {site.Name}…");
                result = await _client.TestSiteAsync(site.Id, _closing);
            });
            if (result is null) return;

            if (result.Problem == SiteProblems.HostKeyNotAccepted)
            {
                if (attempt == 0 && await TrustAsync(site)) continue;
                Say($"{site.Name} wasn't tested, because its server's key isn't trusted.", isProblem: true);
                return;
            }

            if (result.Problem == SiteProblems.CertificateNotTrusted)
            {
                if (attempt == 0 && await TrustCertificateAsync(site)) continue;
                Say($"{site.Name} wasn't used: its server's certificate isn't trusted.", isProblem: true);
                return;
            }

            if (result.Problem == SiteProblems.SignInNeeded)
            {
                // Once: a sign-in that was just made is not offered again at once.
                if (!afterSignIn && attempt == 0 && await AskSignInAsync(site, result.Message)) return;
                Say($"{site.Name}: {result.Message ?? "sign in to use it."}", isProblem: true);
                return;
            }

            if (result.Problem == SiteProblems.HostKeyChanged)
            {
                if (attempt == 0 && await ReplaceKeyAsync(site, result)) continue;
                Say($"{site.Name} wasn't used: its server's key is not the one you trusted.", isProblem: true);
                return;
            }

            if (result.Ok) Say($"{site.Name}: connected, and its start folder was listed.");
            else Say($"{site.Name}: {(string.IsNullOrWhiteSpace(result.Message) ? "the test failed." : result.Message)}", isProblem: true);
            return;
        }
    }

    /// <summary>First use: reads the server's keys without signing in, shows them, and records them if the user trusts them.</summary>
    private async Task<bool> TrustAsync(SiteDto site)
    {
        SiteProbeResultDto? probe = null;
        await RunAsync(async () =>
        {
            Say($"Reading the key of {site.Host}…");
            probe = await _client.ProbeSiteAsync(site.Host, site.Port, _closing);
        });
        if (probe is null) return false;
        if (!probe.Ok || probe.Keys.Count == 0)
        {
            Say($"Couldn't read the key of {site.Host}. {(string.IsNullOrWhiteSpace(probe.Message) ? "Check the server name and the port." : probe.Message)}", isProblem: true);
            return false;
        }

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"{site.Host}{(site.Port == 22 ? "" : ":" + site.Port)} identified itself with the key below. Inbound will connect to this site only if the server shows this key again.",
            TextWrapping = TextWrapping.Wrap,
        });
        AddKeys(body, probe.Keys);
        body.Children.Add(SettingsWindow.Hint("Compare it with the fingerprint your server's administrator gave you, or with the output of ssh-keygen -lf on the server's host key file."));
        if (await AskAsync("Trust this key?", body, "Trust", "Cancel", defaultClose: true) != ContentDialogResult.Primary) return false;
        return await SendTrustAsync(site, probe.Keys);
    }

    /// <summary>A key that is not the trusted one: a hard warning, and a second question before the old key is replaced.</summary>
    private async Task<bool> ReplaceKeyAsync(SiteDto site, SiteResultDto result)
    {
        var warning = new StackPanel { Spacing = 8 };
        warning.Children.Add(new TextBlock
        {
            Text = $"The server {site.Host} now shows a different key from the one you trusted. Someone may be intercepting the connection, or the server may have been reinstalled. Inbound won't sign in with your password or key.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        warning.Children.Add(Mono("Shown now", result.OfferedFingerprint ?? "unknown"));
        foreach (var accepted in result.AcceptedFingerprints) warning.Children.Add(Mono("Trusted", accepted));
        warning.Children.Add(SettingsWindow.Hint("Replace the trusted key only if you or the server's administrator changed the server's keys."));
        if (await AskAsync("The server's key has changed", warning, "Replace the key…", "Cancel", danger: true, defaultClose: true) != ContentDialogResult.Primary)
            return false;

        SiteProbeResultDto? probe = null;
        await RunAsync(async () => probe = await _client.ProbeSiteAsync(site.Host, site.Port, _closing));
        if (probe is null) return false;
        if (!probe.Ok || !SitesRules.OffersFingerprint(probe.Keys, result.OfferedFingerprint))
        {
            Say(probe.Ok ? "The server showed another key just now. Nothing was changed. Test the site again." : $"Couldn't read the key of {site.Host}. {probe.Message}", isProblem: true);
            return false;
        }

        var confirm = new StackPanel { Spacing = 8 };
        confirm.Children.Add(new TextBlock
        {
            Text = "Are you sure? The key you trusted before is forgotten, and these keys are trusted instead.",
            TextWrapping = TextWrapping.Wrap,
        });
        AddKeys(confirm, probe.Keys);
        if (await AskAsync("Replace the trusted key?", confirm, "Replace the key", "Keep the old key", danger: true, defaultClose: true) != ContentDialogResult.Primary)
            return false;
        return await SendTrustAsync(site, probe.Keys);
    }

    private async Task<bool> SendTrustAsync(SiteDto site, IEnumerable<SiteHostKeyDto> keys)
    {
        SiteResultDto? trusted = null;
        await RunAsync(async () => trusted = await _client.TrustSiteAsync(site.Id, keys.Select(key => key.Entry), _closing));
        if (trusted is null) return false;
        if (!trusted.Ok)
        {
            Say(string.IsNullOrWhiteSpace(trusted.Message) ? "The key couldn't be saved." : trusted.Message, isProblem: true);
            return false;
        }

        if (trusted.Site is { } updated)
        {
            var index = Sites.FindIndex(s => s.Id == updated.Id);
            if (index >= 0) Sites[index] = updated;
            Render();
        }

        return true;
    }

    /// <summary>
    /// SITES.6b: the server's own certificate, shown with what it says and its fingerprint; trusted only on a yes, and only
    /// if the server still shows the same one. A certificate that trusting would not fix says why and offers nothing.
    /// </summary>
    private async Task<bool> TrustCertificateAsync(SiteDto site)
    {
        SiteCertificateResultDto? read = null;
        await RunAsync(async () =>
        {
            Say($"Reading the certificate of {site.Host}…");
            read = await _client.SiteCertificateAsync(site.Id, _closing);
        });
        if (read is null) return false;
        if (!read.Ok || read.Certificate is not { } certificate)
        {
            Say($"Couldn't read the certificate of {site.Host}. {read.Message}", isProblem: true);
            return false;
        }

        var body = new StackPanel { Spacing = 8 };
        if (certificate.CannotTrust is { } cannot)
        {
            body.Children.Add(new TextBlock { Text = $"{site.Host} shows a certificate that this PC can't use. {cannot}", TextWrapping = TextWrapping.Wrap });
            foreach (var (label, value) in SitesRules.CertificateLines(certificate)) body.Children.Add(Mono(label, value));
            await AskAsync("Certificate not usable", body, null, "Close");
            return false;
        }

        body.Children.Add(new TextBlock
        {
            Text = $"{site.Host} showed the certificate below, which isn't signed by an authority this PC trusts. That is usual for a server you run yourself. Trust it only if it is your server's: once trusted, Inbound connects only while the server shows a certificate it trusts.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var (label, value) in SitesRules.CertificateLines(certificate)) body.Children.Add(Mono(label, value));
        body.Children.Add(SettingsWindow.Hint("Compare the fingerprint with the one on the server (for example openssl x509 -noout -fingerprint -sha256 -in the certificate file)."));
        // rclone takes a trusted certificate as an authority for all of Inbound's sites (no per-site trust exists).
        body.Children.Add(SettingsWindow.Hint("Whoever holds this certificate's key is trusted by all your Inbound sites, not only this one. Trust only a certificate of your own server."));
        if (await AskAsync("Trust this certificate?", body, "Trust", "Cancel", defaultClose: true) != ContentDialogResult.Primary) return false;

        SiteResultDto? trusted = null;
        await RunAsync(async () => trusted = await _client.TrustSiteCertificateAsync(site.Id, certificate.Fingerprint, _closing));
        if (trusted is null) return false;
        if (!trusted.Ok)
        {
            Say(string.IsNullOrWhiteSpace(trusted.Message) ? "The certificate couldn't be trusted." : trusted.Message, isProblem: true);
            return false;
        }

        if (trusted.Site is { } updated)
        {
            var index = Sites.FindIndex(s => s.Id == updated.Id);
            if (index >= 0) Sites[index] = updated;
            Render();
        }

        if (!string.IsNullOrWhiteSpace(trusted.Message))
        {
            // Trusted, but rclone is busy with transfers and reads the certificate when they finish: a test now would fail.
            Say(trusted.Message);
            return false;
        }

        return true;
    }

    public async Task ForgetCertificateAsync(SiteDto site)
    {
        var body = new TextBlock
        {
            Text = $"Stop trusting the certificate of {site.Host}? The site then can't be used until you trust its certificate again (Test asks).",
            TextWrapping = TextWrapping.Wrap,
        };
        if (await AskAsync("Forget the certificate?", body, "Forget", "Cancel", defaultClose: true) != ContentDialogResult.Primary) return;
        await RunAsync(async () =>
        {
            var result = await _client.TrustSiteCertificateAsync(site.Id, null, _closing);
            if (!result.Ok)
            {
                Say(string.IsNullOrWhiteSpace(result.Message) ? "The certificate couldn't be forgotten." : result.Message, isProblem: true);
                return;
            }

            if (result.Site is { } updated)
            {
                var index = Sites.FindIndex(s => s.Id == updated.Id);
                if (index >= 0) Sites[index] = updated;
                Render();
            }

            Say($"{site.Name} no longer trusts its server's certificate.");
        });
    }

    private static void AddKeys(Panel panel, IEnumerable<SiteHostKeyDto> keys)
    {
        foreach (var key in keys) panel.Children.Add(Mono(key.Type, key.Fingerprint));
    }

    private static TextBlock Mono(string label, string value) => new()
    {
        Text = $"{label}\n{value}",
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Menlo, monospace"),
    };

    private async Task<ContentDialogResult?> AskAsync(string title, UIElement body, string? primary, string close, bool danger = false, bool defaultClose = false)
    {
        if (_window.Content is not FrameworkElement root || root.XamlRoot is null) return null;
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            Title = title,
            Content = DialogLayout.ScrollingContent(body, root.XamlRoot),
            // No primary text: a message with Close only.
            PrimaryButtonText = primary ?? string.Empty,
            CloseButtonText = close,
            DefaultButton = defaultClose || primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        if (danger) dialog.PrimaryButtonStyle = InboundUi.DangerDialogButton;
        return await DialogGuard.ShowAsync(dialog);
    }
}
