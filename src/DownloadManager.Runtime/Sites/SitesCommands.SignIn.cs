using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DownloadManager.Contracts;
using DownloadManager.Persistence;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>
/// SITES.6c: signing in a Drive, OneDrive or Dropbox site (remote-sites.md, "OAuth kinds"). The Host runs
/// <c>rclone authorize &lt;kind&gt; --auth-no-open-browser</c>, hands the address it prints to the window to open, and stores
/// the token it prints at the end, sealed. rclone listens on 127.0.0.1:53682 for the answer, so there is one sign-in at a
/// time, and a browser on another computer uses the paste flow instead (<see cref="SiteSignInRequest.Token"/>).
/// </summary>
public sealed partial class SitesCommands
{
    private static readonly TimeSpan SignInLinkTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SignInTime = TimeSpan.FromMinutes(10);
    private const int MostTokenChars = 16 * 1024;

    private readonly object _signInLock = new();

    private SignInSession? _signIn;
    private long _signInEpoch;

    private sealed class SignInSession(string siteId, Process process, CancellationTokenSource stop, long epoch)
    {
        public string SiteId { get; } = siteId;
        public Process Process { get; } = process;
        public CancellationTokenSource Stop { get; } = stop;
        public long Epoch { get; } = epoch;
    }

    [GeneratedRegex(@"(http://127\.0\.0\.1:\d+/auth\?state=[A-Za-z0-9_\-]+)")]
    private static partial Regex SignInLink();

    private async Task<SiteSignInResultDto> SignInAsync(SiteSignInRequest request, CancellationToken cancellationToken)
    {
        var epoch = Interlocked.Read(ref _signInEpoch);
        if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
            return new SiteSignInResultDto { Message = "That remote no longer exists." };
        if (!SiteKindCatalog.SignsIn(site.Kind)) return new SiteSignInResultDto { Message = "This kind of remote does not sign in through the browser." };
        var command = $"rclone authorize \"{site.Kind}\"" + (site.ClientId is { } id ? $" \"{id}\" \"<your client secret>\"" : "");

        if (request.Token is { } pasted)
        {
            var token = ReadPastedToken(pasted);
            if (token is null)
                return new SiteSignInResultDto { Message = "That is not a token from rclone authorize. Paste everything between ---> and <---End paste.", AuthorizeCommand = command };
            var finished = await FinishSignInAsync(site.Id, token, epoch, cancellationToken).ConfigureAwait(false);
            return new SiteSignInResultDto { Ok = finished.Ok, Message = finished.Message, Site = finished.Site, AuthorizeCommand = command };
        }

        var rclone = _findRclone(_config.OwnRclonePath);
        if (rclone is null) return new SiteSignInResultDto { Message = "rclone is not on this PC yet. Download it in Options > Remote files.", AuthorizeCommand = command };

        SignInSession session;
        StringBuilder output;
        TaskCompletionSource<string?> link;
        // Serialize process creation with disable and credential storage, never the browser wait.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposing || !_config.Enabled || epoch != Interlocked.Read(ref _signInEpoch))
                return new SiteSignInResultDto { Message = OffMessage };
            lock (_signInLock)
            {
                if (_signIn is { } waiting && waiting.SiteId != site.Id && !waiting.Process.HasExited)
                    return new SiteSignInResultDto { Message = "Another remote's sign-in is waiting in the browser. Finish or cancel it first.", AuthorizeCommand = command };
            }

            CancelSignIn(null);
            var info = new ProcessStartInfo(rclone)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "authorize", site.Kind, "--auth-no-open-browser", "--config", "" }) info.ArgumentList.Add(argument);
            RcloneEnvironment.Clean(info.Environment);
            if (site.ClientId is { } clientId)
            {
                // SITES.10: the user's own app goes in rclone's environment (RCLONE_<BACKEND>_CLIENT_ID/_SECRET, read by
                // authorize as by any backend option; checked with 1.76), not on its command line, which any program on
                // this PC can read.
                var prefix = "RCLONE_" + site.Kind.ToUpperInvariant() + "_";
                info.Environment[prefix + "CLIENT_ID"] = clientId;
                info.Environment[prefix + "CLIENT_SECRET"] = site.SealedClientSecret is null ? "" : SecretProtector.UnprotectSiteSecret(site.SealedClientSecret) ?? "";
            }

            Process process;
            try
            {
                process = Process.Start(info) ?? throw new InvalidOperationException("no process");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new SiteSignInResultDto { Message = $"rclone could not be started: {e.Message}", AuthorizeCommand = command };
            }

            var stop = new CancellationTokenSource(SignInTime);
            session = new SignInSession(site.Id, process, stop, epoch);
            lock (_signInLock) _signIn = session;
            link = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            output = new StringBuilder();
            var captured = output;
            var linkCaptured = link;
            process.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is { } text && SignInLink().Match(text) is { Success: true } match) linkCaptured.TrySetResult(match.Groups[1].Value);
            };
            process.OutputDataReceived += (_, line) =>
            {
                if (line.Data is { } text)
                    lock (captured)
                    {
                        if (captured.Length < MostTokenChars * 2) captured.AppendLine(text);
                    }
            };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            _ = WatchSignInAsync(session, output, link);
        }
        finally
        {
            _gate.Release();
        }

        string? url;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            wait.CancelAfter(SignInLinkTime);
            try
            {
                url = await link.Task.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                url = null;
            }
        }

        if (url is null)
        {
            CancelSignIn(site.Id, session);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposing || epoch != Interlocked.Read(ref _signInEpoch))
                return new SiteSignInResultDto { Message = OffMessage };
            return new SiteSignInResultDto
            {
                Message = "rclone did not start the sign-in. Another program may be using port 53682; close it and try again, or use the paste flow.",
                AuthorizeCommand = command,
            };
        }

        if (_disposing || epoch != Interlocked.Read(ref _signInEpoch))
            return new SiteSignInResultDto { Message = OffMessage };
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteSignInResultDto { Ok = true, Url = url, AuthorizeCommand = command };
    }

    /// <summary>Waits for <c>rclone authorize</c> to end; a token it printed signs the site in.</summary>
    private async Task WatchSignInAsync(SignInSession session, StringBuilder output, TaskCompletionSource<string?> link)
    {
        try
        {
            await session.Process.WaitForExitAsync(session.Stop.Token).ConfigureAwait(false);
            // The output read in the background is complete only after this.
            session.Process.WaitForExit();
            link.TrySetResult(null);
            string text;
            lock (output) text = output.ToString();
            // A cancelled sign-in stores nothing, even when rclone printed a token just before.
            if (session.Stop.IsCancellationRequested) return;
            if (session.Process.ExitCode == 0 && ReadPastedToken(text) is { } token)
            {
                var finished = await FinishSignInAsync(session.SiteId, token, session.Epoch, session.Stop.Token).ConfigureAwait(false);
                if (!finished.Ok) _log($"A site sign-in could not be stored: {finished.Message}");
            }
            else _log("A site sign-in ended without a token.");
        }
        catch (OperationCanceledException)
        {
            link.TrySetResult(null);
            Kill(session.Process);
        }
        catch (Exception e)
        {
            _log($"A site sign-in failed: {e.Message}");
        }
        finally
        {
            lock (_signInLock)
            {
                if (ReferenceEquals(_signIn, session)) _signIn = null;
            }

            session.Process.Dispose();
            session.Stop.Dispose();
        }

        // The window waits for the sign-in to end (SiteDto.SigningIn).
        await RaiseChangedAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private SiteDto MarkSigningIn(SiteDto site)
    {
        lock (_signInLock) site.SigningIn = _signIn is { } session && session.SiteId == site.Id;
        return site;
    }

    /// <summary>Stops a waiting sign-in of <paramref name="siteId"/> (or any, with null).</summary>
    private void CancelSignIn(string? siteId, SignInSession? expected = null)
    {
        SignInSession? session;
        lock (_signInLock)
        {
            session = _signIn;
            if (session is null || (siteId is not null && session.SiteId != siteId)
                || (expected is not null && !ReferenceEquals(expected, session))) return;
            _signIn = null;
        }

        try
        {
            session.Stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It ended meanwhile.
        }

        Kill(session.Process);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private async Task<SiteResultDto> SignInCancelAsync(string id)
    {
        CancelSignIn(id);
        await Task.CompletedTask.ConfigureAwait(false);
        return new SiteResultDto { Ok = true };
    }

    /// <summary>
    /// The token in what <c>rclone authorize</c> printed (between <c>---&gt;</c> and <c>&lt;---End paste</c>), or a token pasted
    /// on its own: a JSON object with an access token. Null when it is neither.
    /// </summary>
    public static string? ReadPastedToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MostTokenChars * 2) return null;
        var start = text.IndexOf("--->", StringComparison.Ordinal);
        var end = text.IndexOf("<---End paste", StringComparison.Ordinal);
        var candidate = (start >= 0 && end > start ? text[(start + 4)..end] : text).Trim();
        // rclone 1.6x+ may print the token as base64 of the JSON; both are accepted.
        if (!candidate.StartsWith('{'))
        {
            try
            {
                candidate = Encoding.UTF8.GetString(Convert.FromBase64String(candidate)).Trim();
            }
            catch (FormatException)
            {
                return null;
            }
        }

        try
        {
            if (JsonNode.Parse(candidate) is not JsonObject token || token["access_token"]?.GetValue<string>() is not { Length: > 0 }) return null;
            // rclone's authorize may wrap the token with a config blob: the token itself is what is kept.
            return token.ToJsonString();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Seals the new token (and, for OneDrive, the drive it opens) into the site.</summary>
    private async Task<SiteResultDto> FinishSignInAsync(string siteId, string token, long epoch, CancellationToken cancellationToken)
    {
        if (await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false) is not { } site) return Fail("That remote no longer exists.");
        var settings = new JsonObject { ["token"] = token };
        if (site.Kind == SiteKinds.Drive) settings["scope"] = "drive";
        if (site.Kind == SiteKinds.OneDrive)
        {
            var (driveId, driveType, problem) = await _oneDriveDrive(token, cancellationToken).ConfigureAwait(false);
            if (problem is not null) return Fail(problem);
            settings["drive_id"] = driveId;
            settings["drive_type"] = driveType;
        }

        SiteRecord saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposing || !_config.Enabled || epoch != Interlocked.Read(ref _signInEpoch)) return Fail(OffMessage);
            if (await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false) is not { } current) return Fail("That remote no longer exists.");
            saved = current with { SealedSignIn = SecretProtector.ProtectSiteSecret(settings.ToJsonString()), ModifiedAt = DateTimeOffset.UtcNow };
            await _sites.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        catch (SiteStoreException e)
        {
            return Fail(e.Message);
        }
        finally
        {
            _gate.Release();
        }

        ForgetListings(siteId);
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto { Ok = true, Site = ToDto(saved) };
    }

    /// <summary>
    /// rclone renewed a site's token: the new settings are sealed and stored, unless the site was signed in again or
    /// removed meanwhile.
    /// </summary>
    private async Task SaveRenewedSignInAsync(string siteId, string previousToken, JsonObject settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _sites.GetAsync(siteId, cancellationToken).ConfigureAwait(false) is not { SealedSignIn: not null } current) return;
            // Signed in again meanwhile: the renewal of the older grant is not kept.
            if (StoredToken(current.SealedSignIn) != previousToken) return;
            var saved = current with { SealedSignIn = SecretProtector.ProtectSiteSecret(settings.ToJsonString()) };
            await _sites.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
            _log("A site's sign-in was renewed by its provider and saved.");
        }
        catch (SiteStoreException e)
        {
            _log($"A renewed sign-in could not be saved: {e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? StoredToken(string sealedSignIn)
    {
        try
        {
            return SecretProtector.UnprotectSiteSecret(sealedSignIn) is { } json ? JsonNode.Parse(json)?["token"]?.GetValue<string>() : null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The signed-in user's OneDrive (Microsoft Graph <c>/me/drive</c>), which rclone needs by id and type.</summary>
    private static async Task<(string? Id, string? Type, string? Problem)> ReadOneDriveDriveAsync(string token, CancellationToken cancellationToken)
    {
        string? access;
        try
        {
            access = JsonNode.Parse(token)?["access_token"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            access = null;
        }

        if (access is null) return (null, null, "The sign-in gave no access token.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, null, $"OneDrive did not say which drive to open ({(int)response.StatusCode}). Sign in again.");
            var drive = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var id = drive?["id"]?.GetValue<string>();
            var type = drive?["driveType"]?.GetValue<string>();
            return id is null || type is null ? (null, null, "OneDrive did not say which drive to open. Sign in again.") : (id, type, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, null, "OneDrive could not be reached to find your drive. Check the network and sign in again.");
        }
    }
}
