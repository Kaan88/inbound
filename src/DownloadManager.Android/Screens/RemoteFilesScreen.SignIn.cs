using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;

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

    private bool CanSignIn(SiteDto site) =>
        SameHost() && !_busy && _secureHostConnection && IsSignInKind(site.Kind) && !site.SigningIn
        && (!site.SignedIn || _expiredCloudSites.Contains(site.Id));

    private static bool IsSignInKind(string? kind) =>
        string.Equals(kind, SignInKindDrive, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, SignInKindOneDrive, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, SignInKindDropbox, StringComparison.OrdinalIgnoreCase);

    /// <summary>Add to the cloud location card's action row; signed-in cards do not show this action.</summary>
    private View? CreateCloudSignInAction(SiteDto site)
    {
        if (!IsSignInKind(site.Kind) || site.SignedIn && !site.SigningIn && !_expiredCloudSites.Contains(site.Id)) return null;

        var button = new MaterialButton(_activity, null, Resource.Attribute.materialButtonStyle)
        {
            Text = site.SigningIn ? "Waiting" : "Sign in",
            Enabled = CanSignIn(site)
        };
        button.SetAllCaps(false);
        button.Click += (_, _) =>
        {
            if (CanSignIn(site)) BeginCloudSignIn(site);
        };
        button.ContentDescription = site.SigningIn ? $"Waiting for sign-in to {site.Name}" : $"Sign in to {site.Name}";
        return button;
    }

    private void BeginCloudSignIn(SiteDto site)
    {
        if (!CanSignIn(site) || _signInWait is not null) return;
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

    private void ShowPhoneSignInWait(SiteDto site)
    {
        if (!SameHost()) return;
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        body.SetPadding(Motion.Dp(_activity, 20), Motion.Dp(_activity, 8), Motion.Dp(_activity, 20), 0);
        body.AddView(Label($"Sign in to {site.Name} in the browser. This closes when the phone finishes.", 15,
            Resource.Attribute.colorOnSurfaceVariant));
        _phoneWaitDialog = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Signing in")!
            .SetView(body)!
            .SetNegativeButton("Cancel", (_, _) => CancelCloudSignIn(site.Id))!
            .Create();
        _phoneWaitDialog.DismissEvent += (_, _) => _phoneWaitDialog = null;
        _phoneWaitDialog.CancelEvent += (_, _) => CancelCloudSignIn(site.Id);
        _phoneWaitDialog.Show();
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
                {
                    // A stored token alone does not distinguish an old rejected grant from the new one.
                    // Allow another browse to re-check the provider and mark it expired again if necessary.
                    _expiredCloudSites.Remove(site.Id);
                }
                _ = LoadLocationsAsync();
                return;
            }
        }
    }

    private void ShowPasteTokenDialog(SiteDto site, string command)
    {
        if (!SameHost()) return;
        _signInDialog?.Dismiss();
        _signInDialog = null;
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        body.SetPadding(Motion.Dp(_activity, 20), Motion.Dp(_activity, 8), Motion.Dp(_activity, 20), 0);
        body.AddView(Label(site.ClientId is null
            ? $"The token will be sent to {_hostDisplay}. Run this command on a computer with rclone and a browser, then paste the output between ---> and <---End paste. The token gives access to your account."
            : $"The token will be sent to {_hostDisplay}. Use your own app registration credentials in place of the literal placeholders on a computer with rclone and a browser. Never paste your client secret here. Then paste rclone's output between ---> and <---End paste.", 14,
            Resource.Attribute.colorOnSurfaceVariant));
        var commandField = new EditText(_activity) { Text = command, SaveEnabled = false, Enabled = false };
        commandField.SetSingleLine(false);
        body.AddView(commandField);
        var token = _signInToken = new EditText(_activity)
        {
            Hint = "Paste authorization token",
            SaveEnabled = false,
            InputType = InputTypes.ClassText | InputTypes.TextVariationPassword
        };
        token.SetSingleLine(true);
        token.ImportantForAutofill = Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O
            ? ImportantForAutofill.No : token.ImportantForAutofill;
        body.AddView(token);

        _signInDialog = new MaterialAlertDialogBuilder(_activity)
            .SetTitle($"Sign in to {site.Name}")!
            .SetView(body)!
            .SetNegativeButton("Cancel", (_, _) => ClearSignInToken())!
            .SetPositiveButton("Sign in", (_, _) =>
            {
                string pasted = token.Text ?? "";
                ClearSignInToken();
                _ = SubmitCloudTokenAsync(site, pasted);
            })!
            .Create();
        _signInDialog.DismissEvent += (_, _) => { ClearSignInToken(); _signInDialog = null; };
        _signInDialog.CancelEvent += (_, _) => CancelCloudSignIn(site.Id);
        _signInDialog.Show();
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
            _expiredCloudSites.Remove(site.Id);
            _ = LoadLocationsAsync();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost()) ShowCloudSignInError("Could not confirm sign-in. Refresh the location list before trying again.");
        }
        finally { token = ""; FinishSignInWait(site.Id); }
    }

    private void CancelCloudSignIn(string siteId)
    {
        _signInWait?.Cancel();
        _signInDialog?.Dismiss();
        _signInDialog = null;
        _phoneWaitDialog?.Dismiss();
        _phoneWaitDialog = null;
        ClearSignInToken();
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
        _signInSiteId = null;
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
        _activity.RunOnUiThread(() => { if (SameHost()) Render(); });
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
