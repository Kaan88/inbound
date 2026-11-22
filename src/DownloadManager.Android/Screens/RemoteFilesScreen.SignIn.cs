using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>
/// Cloud sign-in controls and lifecycle. The caller still owns the selected Host connection;
/// every request uses this screen's existing RemoteFilesClient and lifetime token.
/// </summary>
internal sealed partial class RemoteFilesScreen
{
    private const string SignInKindDrive = "drive";
    private const string SignInKindOneDrive = "onedrive";
    private const string SignInKindDropbox = "dropbox";
    private CancellationTokenSource? _signInWait;
    private CancellationTokenRegistration _signInCancellation;
    private string? _signInSiteId;
    private readonly HashSet<string> _expiredCloudSites = new(StringComparer.Ordinal);
    private bool _ownsPhoneSignIn;
    private EditText? _signInToken;
    private AndroidX.AppCompat.App.AlertDialog? _signInDialog;
    private AndroidX.AppCompat.App.AlertDialog? _phoneWaitDialog;

    private bool NeedsCloudSignIn(SiteDto site) =>
        IsSignInKind(site.Kind) && (!site.SignedIn || _expiredCloudSites.Contains(site.Id));

    private string? _signInAgain;
    private Uri? _signInUri;

    private bool CanSignIn(SiteDto site) =>
        SameHost() && !_busy && _secureHostConnection && !site.SigningIn && (NeedsCloudSignIn(site) || _signInAgain == site.Id);

    private static bool IsSignInKind(string? kind) =>
        string.Equals(kind, SignInKindDrive, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, SignInKindOneDrive, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, SignInKindDropbox, StringComparison.OrdinalIgnoreCase);

    /// <summary>Add to the cloud location card's action row; signed-in cards do not show this action.</summary>
    private View? CreateCloudSignInAction(SiteDto site)
    {
        if (!IsSignInKind(site.Kind) || site.SignedIn && !site.SigningIn && !_expiredCloudSites.Contains(site.Id)) return null;

        var button = new MaterialButton(_activity, null, Resource.Attribute.materialButtonTonalStyle)
        {
            Text = site.SigningIn ? "Waiting" : "Sign in",
            Enabled = CanSignIn(site) || site.SigningIn,
            Alpha = site.SigningIn ? 0.6f : 1f
        };
        button.SetAllCaps(false);
        button.Click += (_, _) =>
        {
            if (CanSignIn(site)) BeginCloudSignIn(site);
        };
        button.ContentDescription = site.SigningIn ? $"Waiting for sign-in to {site.Name}" : $"Sign in to {site.Name}";
        return button;
    }

    private void BeginCloudSignIn(SiteDto site, bool again = false)
    {
        _signInAgain = again ? site.Id : null;
        if (!CanSignIn(site) || _signInWait is not null) { _signInAgain = null; return; }
        _signInSiteId = site.Id;
        _signInWait = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _signInCancellation = _signInWait.Token.Register(() =>
        {
            if (_ownsPhoneSignIn) _ = CancelCloudSignInOnHostAsync(site.Id);
            _activity.RunOnUiThread(() =>
            {
                _phoneWaitDialog?.Dismiss();
                _phoneWaitDialog = null;
            });
        });

        if (_phoneHost)
            _ = StartPhoneCloudSignInAsync(site, _signInWait.Token);
        else
            ShowPairedHostPasteFlow(site, _signInWait.Token);
    }

    private async Task StartPhoneCloudSignInAsync(SiteDto site, CancellationToken cancellationToken)
    {
        try
        {
            var started = await _client.SignInSiteAsync(site.Id, cancellationToken: cancellationToken);
            if (!started.Ok || started.Url is null)
            {
                if (!SameHost() || cancellationToken.IsCancellationRequested) return;
                ShowCloudSignInError(started.Message ?? "The phone could not start a safe sign-in link.");
                return;
            }
            _ownsPhoneSignIn = true;
            if (!SameHost() || cancellationToken.IsCancellationRequested)
            {
                _ = CancelCloudSignInOnHostAsync(site.Id);
                return;
            }
            if (!TryGetSafeRcloneLoopbackUrl(started.Url, out var authUri))
            {
                _ = CancelCloudSignInOnHostAsync(site.Id);
                ShowCloudSignInError("The Host returned an unsafe sign-in address. It was not opened.");
                return;
            }

            try
            {
                if (!SameHost() || cancellationToken.IsCancellationRequested)
                {
                    _ = CancelCloudSignInOnHostAsync(site.Id);
                    return;
                }
                _signInUri = authUri;
                _activity.StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(authUri.AbsoluteUri)));
            }
            catch (Exception e) when (e is ActivityNotFoundException or InvalidOperationException or Java.Lang.SecurityException)
            {
                _ = CancelCloudSignInOnHostAsync(site.Id);
                ShowCloudSignInError("No browser could open the sign-in link. You can use the token option instead.");
                return;
            }

            ShowPhoneSignInWait(site);
            await WaitForPhoneSignInAsync(site, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost() && !cancellationToken.IsCancellationRequested)
                ShowCloudSignInError("Could not start the cloud sign-in. Check this phone's connection and try again.");
        }
        finally { FinishSignInWait(site.Id); }
    }

    private void ShowPairedHostPasteFlow(SiteDto site, CancellationToken cancellationToken)
    {
        try
        {
            // A remote rclone process listens on the Host's own loopback. Never launch its
            // 127.0.0.1 URL on this phone; a paired Host uses only its paste-token flow.
            if (!_secureHostConnection)
            {
                ShowCloudSignInError("Sign in to a paired Host only over verified HTTPS.");
                return;
            }

            if (!SameHost() || cancellationToken.IsCancellationRequested) return;
            ShowPasteTokenDialog(site, BuildAuthorizeCommand(site));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost() && !cancellationToken.IsCancellationRequested)
                ShowCloudSignInError("Could not prepare cloud sign-in. Check the Host connection and try again.");
        }
        finally { FinishSignInWait(site.Id); }
    }

    /// <summary>x28: spinner, "Signing in to X", the browser instruction, Open browser again and Cancel.</summary>
    private void ShowPhoneSignInWait(SiteDto site)
    {
        if (!SameHost()) return;
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        body.SetGravity(GravityFlags.CenterHorizontal);
        body.SetPadding(Dp(24), Dp(24), Dp(24), 0);
        var spinner = new RingSpinner(_activity, 4, Attr(Resource.Attribute.colorOutlineVariant), Attr(Resource.Attribute.colorPrimary)) { ContentDescription = "Signing in" };
        body.AddView(spinner, new LinearLayout.LayoutParams(Dp(44), Dp(44)) { Gravity = GravityFlags.CenterHorizontal });
        var heading = FolderText($"Signing in to {site.Name}", 22, Resource.Attribute.colorOnSurface);
        heading.Gravity = GravityFlags.Center;
        heading.SetPadding(0, Dp(16), 0, Dp(8));
        body.AddView(heading, new LinearLayout.LayoutParams(-1, -2));
        var message = FolderText("Finish signing in with the browser. This closes by itself when the phone has the authorization.", 14,
            Resource.Attribute.colorOnSurfaceVariant);
        message.Gravity = GravityFlags.Center;
        message.SetLineSpacing(Dp(3), 1f);
        body.AddView(message, new LinearLayout.LayoutParams(-1, -2));
        var dialog = new MaterialAlertDialogBuilder(_activity)
            .SetView(body)!
            .SetNegativeButton("Open browser again", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Cancel", (_, _) => CancelCloudSignIn(site.Id))!
            .Create();
        _phoneWaitDialog = dialog;
        dialog.ShowEvent += (_, _) => dialog.GetButton((int)Android.Content.DialogButtonType.Negative)!.Click += (_, _) =>
        {
            try
            {
                if (_signInUri is { } uri)
                    _activity.StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(uri.AbsoluteUri)));
            }
            catch (Exception e) when (e is ActivityNotFoundException or InvalidOperationException or Java.Lang.SecurityException)
            {
                _activity.ShowMessage("No browser could open the sign-in link.");
            }
        };
        var fab = _surface.FindViewWithTag("rf_fab");
        if (fab is not null) fab.Visibility = ViewStates.Gone; // x28 draws no button behind the dialog
        dialog.DismissEvent += (_, _) =>
        {
            if (fab is not null) fab.Visibility = ViewStates.Visible;
            if (ReferenceEquals(_phoneWaitDialog, dialog)) _phoneWaitDialog = null;
        };
        dialog.CancelEvent += (_, _) => CancelCloudSignIn(site.Id);
        dialog.Show();
    }

    private async Task WaitForPhoneSignInAsync(SiteDto site, CancellationToken cancellationToken)
    {
        while (SameHost() && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            var list = await _client.ListAsync(cancellationToken);
            if (!SameHost() || cancellationToken.IsCancellationRequested) return;
            var latest = list.Sites.FirstOrDefault(s => s.Id == site.Id);
            if (latest is not { SigningIn: true })
            {
                _phoneWaitDialog?.Dismiss();
                _phoneWaitDialog = null;
                if (latest is { SignedIn: true })
                    await VerifyPhoneCloudSignInAsync(site.Id, cancellationToken);
                else if (ViewingCloudLocations) _ = LoadLocationsAsync();
                return;
            }
        }
    }

    private bool ViewingCloudLocations => !_inFolder && !_adding && !_showTransfers;

    private bool ViewingCloudFolder(string siteId) =>
        _inFolder && ShowsSite(siteId) && !_adding && !_showTransfers;

    /// <summary>A failed transfer asked for this sign-in (x2): once it is done, the transfer is retried.</summary>
    private void RetryAfterSignIn(string siteId)
    {
        if (_retryAfterSignIn is not { } retry || retry.SiteId != siteId) return;
        _retryAfterSignIn = null;
        // A retry left by a sign-in that failed can be picked up by a later one: only a transfer still failed is retried (Fable N1).
        if (_transfers.FirstOrDefault(t => t.Id == retry.TransferId) is not { State: "failed" }) return;
        _ = TransferActionAsync(retry.TransferId, "retry");
    }

    private async Task VerifyPhoneCloudSignInAsync(string siteId, CancellationToken cancellationToken)
    {
        RetryAfterSignIn(siteId);
        if (ViewingCloudFolder(siteId))
        {
            await AfterFolderSignInAsync(siteId);
            return;
        }
        if (!ViewingCloudLocations) return;
        if (_expiredCloudSites.Contains(siteId))
        {
            // OAuth ending is ambiguous when the old rejected token is still stored.
            int generation = _generation;
            var verification = await _client.BrowseAsync(new SiteBrowseRequest
            {
                Id = siteId, Limit = 1, Refresh = true
            }, cancellationToken);
            if (!SameHost() || cancellationToken.IsCancellationRequested || generation != _generation || !ViewingCloudLocations) return;
            if (verification.Ok) _expiredCloudSites.Remove(siteId);
        }
        await LoadLocationsAsync();
    }

    /// <summary>x29 and x29b: the command in a monospace box with copy, the token field with paste, Cancel and Sign in.</summary>
    private void ShowPasteTokenDialog(SiteDto site, string command)
    {
        if (!SameHost()) return;
        _signInDialog?.Dismiss();
        _signInDialog = null;
        bool own = site.ClientId is not null;
        var dialog = NewSheet(out var column);
        SheetTitle(column, $"Sign in to {site.Name}");
        string text = own
            ? $"On a computer with rclone and a browser, run this command with your own client ID and secret in place of the placeholders. Then paste everything between ---> and <---End paste. The token is sent to {_hostName}."
            : $"On a computer with rclone and a browser, run this command. Then paste everything between ---> and <---End paste. The token is sent to {_hostName} and gives access to your account.";
        var intro = FolderText(text, 14, Resource.Attribute.colorOnSurfaceVariant);
        intro.SetLineSpacing(Dp(3), 1f);
        AddInset(column, intro);
        AddInset(column, CopyBox(command, "Copy command"));
        if (own)
        {
            var never = FolderText("Never paste your client secret here.", 13, Resource.Attribute.colorOnSurfaceVariant);
            never.SetPadding(Dp(4), 0, 0, Dp(4));
            AddInset(column, never);
        }
        var (layout, token) = OutlinedField("Authorization token", "", InputTypes.ClassText, password: true, placeholder: "Paste authorization token");
        _signInToken = token;
        token.ImportantForAutofill = ImportantForAutofill.No;
        layout.EndIconMode = TextInputLayout.EndIconCustom;
        layout.SetEndIconDrawable(Resource.Drawable.ic_paste);
        layout.EndIconContentDescription = "Paste token";
        layout.SetEndIconOnClickListener(new ClickAction(() =>
        {
            try
            {
                if (_activity.GetSystemService(Context.ClipboardService) is Android.Content.ClipboardManager { PrimaryClip: { ItemCount: > 0 } clip }
                    && clip.GetItemAt(0)?.Text is { } pasted)
                    token.Text = pasted;
            }
            catch (Exception) { /* Nothing to paste. */ }
        }));
        AddInset(column, layout);
        var ok = SheetButtons(column, "Sign in", 0, null, () => CancelCloudSignIn(site.Id));
        ok.SetOnClickListener(new ClickAction(() =>
        {
            string pasted = token.Text ?? "";
            if (string.IsNullOrWhiteSpace(pasted)) { SetFieldError(layout, "Paste the authorization token."); return; }
            ClearSignInToken();
            var open = _sheet; _sheet = null; open?.Dismiss();
            _ = SubmitCloudTokenAsync(site, pasted);
        }));
        dialog.DismissEvent += (_, _) => ClearSignInToken();
        dialog.CancelEvent += (_, _) => CancelCloudSignIn(site.Id);
        ShowSheet(dialog);
    }

    private async Task SubmitCloudTokenAsync(SiteDto site, string token)
    {
        try
        {
            if (!SameHost() || !_secureHostConnection || string.IsNullOrWhiteSpace(token)) return;
            var result = await _client.SignInSiteAsync(site.Id, token, _life.Token);
            if (!SameHost()) return;
            if (!result.Ok)
            {
                ShowCloudSignInError("The token was not accepted. Check it and try again.");
                return;
            }
            RefreshAfterCloudSignIn(site.Id);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost()) ShowCloudSignInError("Could not confirm sign-in. Refresh the location list before trying again.");
        }
        finally { token = ""; FinishSignInWait(site.Id); }
    }

    private void RefreshAfterCloudSignIn(string siteId)
    {
        _expiredCloudSites.Remove(siteId);
        RetryAfterSignIn(siteId);
        if (ViewingCloudFolder(siteId) || _inFolder && _pendingChoice is { } pending && pending.SiteId == siteId) _ = AfterFolderSignInAsync(siteId);
        else if (ViewingCloudLocations) _ = LoadLocationsAsync();
    }

    private void CancelCloudSignIn(string siteId)
    {
        if (_retryAfterSignIn is { } retry && retry.SiteId == siteId) _retryAfterSignIn = null;
        _signInWait?.Cancel();
        _signInDialog?.Dismiss();
        _signInDialog = null;
        _phoneWaitDialog?.Dismiss();
        _phoneWaitDialog = null;
        ClearSignInToken();
        _signInAgain = null;
        FinishSignInWait(siteId);
    }

    private void CloseCloudSignInUi()
    {
        if (_signInSiteId is { } siteId) CancelCloudSignIn(siteId);
        else
        {
            _signInDialog?.Dismiss();
            _phoneWaitDialog?.Dismiss();
            ClearSignInToken();
        }
    }

    private async Task CancelCloudSignInOnHostAsync(string siteId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _client.CancelSiteSignInAsync(siteId, timeout.Token); }
        catch (Exception) { /* A stale Host session is harmless; the Host also times out. */ }
    }

    private void FinishSignInWait(string siteId)
    {
        if (_signInSiteId != siteId) return;
        // A pending retry is kept: on a paired Host the token is accepted after this wait ends (Fable M1).
        _signInSiteId = null;
        _signInAgain = null;
        _signInWait?.Dispose();
        _signInWait = null;
        _signInCancellation.Dispose();
        _ownsPhoneSignIn = false;
        if (!_closed && SameHost()) _activity.RunOnUiThread(() => { if (!_closed) Render(); });
    }

    private void ClearSignInToken()
    {
        if (_signInToken is not null) _signInToken.Text = "";
        _signInToken = null;
    }

    private void ShowCloudSignInError(string message)
    {
        if (!SameHost()) return;
        _error = message;
        _activity.RunOnUiThread(() =>
        {
            if (!SameHost()) return;
            if (_inFolder) Toast.MakeText(_activity, message, ToastLength.Long)?.Show(); else Render();
        });
    }

    private static string BuildAuthorizeCommand(SiteDto site)
    {
        string kind = site.Kind.ToLowerInvariant() switch
        {
            SignInKindDrive => SignInKindDrive,
            SignInKindOneDrive => SignInKindOneDrive,
            SignInKindDropbox => SignInKindDropbox,
            _ => throw new InvalidOperationException("Unsupported cloud sign-in kind.")
        };
        return site.ClientId is null
            ? $"rclone authorize \"{kind}\" --auth-no-open-browser"
            : $"rclone authorize \"{kind}\" \"<your client ID>\" \"<your client secret>\" --auth-no-open-browser";
    }

    private static bool TryGetSafeRcloneLoopbackUrl(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate))
            return false;
        if (candidate.Scheme != Uri.UriSchemeHttp
            || candidate.IsDefaultPort
            || candidate.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || candidate.Query.Length < 8
            || !candidate.AbsolutePath.Equals("/auth", StringComparison.Ordinal)
            || !(candidate.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                 || candidate.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                 || candidate.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
                 || candidate.Host.Equals("::1", StringComparison.OrdinalIgnoreCase)))
            return false;

        var query = candidate.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (query.Length != 1 || !query[0].StartsWith("state=", StringComparison.Ordinal)
            || query[0].Length <= "state=".Length
            || query[0]["state=".Length..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            return false;
        uri = candidate;
        return true;
    }
}
