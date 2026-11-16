using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.6c: signing in a Drive, OneDrive or Dropbox site. The Host runs <c>rclone authorize</c>; this page opens the
/// address it gives in the browser and waits, with Cancel, until the Host says the sign-in ended. A browser on another
/// computer uses the paste flow: <c>rclone authorize</c> there, and its token pasted here.
/// </summary>
internal sealed partial class SitesOptionsPage
{
    private static readonly TimeSpan SignInPoll = TimeSpan.FromSeconds(1.5);

    /// <summary>Signs the site in; then tests it. False when it was cancelled or failed (the result line says why).</summary>
    private async Task<bool> SignInAsync(SiteDto site)
    {
        SiteSignInResultDto? started = null;
        await RunAsync(async () =>
        {
            _siteResult.Say($"Starting the sign-in of {site.Name}…");
            started = await _client.SignInSiteAsync(site.Id, cancellationToken: _closing.Token);
        });
        if (started is null) return false;
        if (!started.Ok || started.Url is null)
        {
            // rclone could not listen for the answer (or is missing): the paste flow still works.
            _siteResult.Say(started.Message ?? $"{site.Name} could not be signed in.", isProblem: true);
            return await PasteTokenAsync(site, started.AuthorizeCommand, started.Message);
        }

        var opened = false;
        try
        {
            opened = await Windows.System.Launcher.LaunchUriAsync(new Uri(started.Url));
        }
        catch (Exception e) when (e is UriFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            opened = false;
        }

        var ended = await WaitForSignInAsync(site, started.Url, opened, started.AuthorizeCommand);
        if (ended == SignInEnd.Paste)
        {
            await CancelOnHostAsync(site.Id);
            return await PasteTokenAsync(site, started.AuthorizeCommand, null);
        }

        if (ended == SignInEnd.Cancelled)
        {
            await CancelOnHostAsync(site.Id);
            _siteResult.Say($"The sign-in of {site.Name} was cancelled.");
            return false;
        }

        return await AfterSignInAsync(site.Id);
    }

    /// <summary>Also when the page is closing: the Host's rclone would otherwise hold the sign-in port for minutes.</summary>
    private async Task CancelOnHostAsync(string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _client.CancelSiteSignInAsync(id, timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private enum SignInEnd { Ended, Cancelled, Paste }

    /// <summary>A test found the site not signed in (or no longer accepted): asks, and signs in. True when it was.</summary>
    private async Task<bool> AskSignInAsync(SiteDto site, string? why)
    {
        var body = new TextBlock { Text = why ?? $"{site.Name} needs a sign-in.", TextWrapping = TextWrapping.Wrap };
        if (await AskAsync($"Sign in to {site.Name}?", body, "Sign in…", "Not now") != ContentDialogResult.Primary) return false;
        return await SignInAsync(site);
    }

    /// <summary>A dialog while the browser is used; it closes by itself when the Host's sign-in ends.</summary>
    private async Task<SignInEnd> WaitForSignInAsync(SiteDto site, string url, bool opened, string? command)
    {
        if (_window.Content is not FrameworkElement root || root.XamlRoot is null) return SignInEnd.Cancelled;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = opened
                ? $"Sign in to {site.Name} in the browser window that opened, and allow access. This closes by itself when it is done."
                : $"Open this address in a browser on this PC, sign in to {site.Name} and allow access. This closes by itself when it is done.",
            TextWrapping = TextWrapping.Wrap,
        });
        var address = new TextBox { Text = url, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(address, "Sign-in address");
        body.Children.Add(address);
        body.Children.Add(SettingsWindow.Hint(
            "The browser must be on this PC: it hands the answer to 127.0.0.1. Signing in from another computer? Choose \"Paste a token\"."));

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            Title = $"Signing in to {site.Name}",
            Content = DialogLayout.ScrollingContent(body, root.XamlRoot),
            PrimaryButtonText = command is null ? string.Empty : "Paste a token…",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var ended = false;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var watch = Task.Run(async () =>
        {
            try
            {
                // The Host marks the site SigningIn until rclone authorize ends (with a token, or not).
                await Task.Delay(SignInPoll, waiting.Token);
                while (!waiting.IsCancellationRequested)
                {
                    var list = await _client.ListSitesAsync(waiting.Token);
                    // Only an answer ends the wait; a failed read is tried again.
                    if (list.Value is { } now && now.Sites.FirstOrDefault(s => s.Id == site.Id) is not { SigningIn: true })
                    {
                        ended = true;
                        dialog.DispatcherQueue.TryEnqueue(dialog.Hide);
                        return;
                    }

                    await Task.Delay(SignInPoll, waiting.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var answer = await DialogGuard.ShowAsync(dialog);
        waiting.Cancel();
        await watch;
        if (ended) return SignInEnd.Ended;
        return answer == ContentDialogResult.Primary ? SignInEnd.Paste : SignInEnd.Cancelled;
    }

    /// <summary>The paste flow: <c>rclone authorize</c> on a computer with a browser, and its output pasted here.</summary>
    private async Task<bool> PasteTokenAsync(SiteDto site, string? command, string? problem)
    {
        if (command is null) return false;
        var body = new StackPanel { Spacing = 8 };
        if (problem is not null) body.Children.Add(new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock
        {
            Text = "On a computer with a browser and rclone, run this command, sign in, and paste what it prints between ---> and <---End paste:",
            TextWrapping = TextWrapping.Wrap,
        });
        var commandBox = new TextBox { Text = command, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(commandBox, "rclone authorize command");
        body.Children.Add(commandBox);
        var token = new TextBox { Header = "Token", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 160 };
        AutomationProperties.SetName(token, "Pasted token");
        body.Children.Add(token);
        body.Children.Add(SettingsWindow.Hint("The token gives access to your account: paste it only here. It is stored sealed on this PC."));

        if (await AskAsync($"Sign in to {site.Name} with a token", body, "Sign in", "Cancel") != ContentDialogResult.Primary) return false;
        var pasted = token.Text;
        token.Text = string.Empty;
        SiteSignInResultDto? result = null;
        await RunAsync(async () =>
        {
            _siteResult.Say($"Signing in to {site.Name}…");
            result = await _client.SignInSiteAsync(site.Id, pasted, _closing.Token);
        });
        if (result is null) return false;
        if (!result.Ok)
        {
            _siteResult.Say(result.Message ?? $"{site.Name} could not be signed in.", isProblem: true);
            return false;
        }

        return await AfterSignInAsync(site.Id);
    }

    /// <summary>Reads the site again and, when it holds a token now, tests it.</summary>
    private async Task<bool> AfterSignInAsync(string id)
    {
        var list = await _client.ListSitesAsync(_closing.Token);
        if (list.Value is { } sites) _sites = [.. sites.Sites];
        Render();
        if (_sites.FirstOrDefault(s => s.Id == id) is not { } site) return false;
        if (!site.SignedIn)
        {
            _siteResult.Say($"{site.Name} is not signed in: the browser sign-in ended without access.", isProblem: true);
            return false;
        }

        await TestSiteAsync(site, afterSignIn: true);
        return true;
    }
}
