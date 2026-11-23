using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.6c on the Host: saving the sign-in kinds and B2, the paste flow, and a real <c>rclone authorize</c> started and
/// cancelled (it listens on 127.0.0.1:53682 and prints the address; no browser is opened and nothing reaches the
/// provider). Skipped without rclone.
/// </summary>
public sealed partial class SiteTransfersEndToEndTests
{
    private const string PastedToken = """{"access_token":"made-up-access-0009","token_type":"bearer","refresh_token":"made-up-refresh-0009","expiry":"2030-01-01T00:00:00Z"}""";

    [SiteTransfersFact]
    public async Task ABrowserSignInStartsWithItsAddressIsOneAtATimeAndCancels()
    {
        var commands = Open(null);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        var box = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest { Name = "Box", Kind = SiteKinds.Dropbox });
        Assert.True(box.Ok, box.Message);
        Assert.False(box.Site!.SignedIn);
        Assert.True(box.Site.Encrypted);
        var other = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest { Name = "Drive", Kind = SiteKinds.Drive });

        var needs = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = box.Site.Id });
        Assert.Equal(SiteProblems.SignInNeeded, needs.Problem);

        var started = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, new SiteSignInRequest { Id = box.Site.Id });
        Assert.True(started.Ok, started.Message);
        Assert.StartsWith("http://127.0.0.1:53682/auth?state=", started.Url);
        Assert.Contains("rclone authorize \"dropbox\"", started.AuthorizeCommand);
        var listed = await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { });
        Assert.True(listed.Sites.Single(s => s.Id == box.Site.Id).SigningIn);

        var refused = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, new SiteSignInRequest { Id = other.Site!.Id });
        Assert.False(refused.Ok);
        Assert.Contains("Another remote's sign-in", refused.Message);

        var cancelled = await Call<SiteResultDto>(commands, SitesMessageTypes.SignInCancel, new SiteIdRequest { Id = box.Site.Id });
        Assert.True(cancelled.Ok);
        listed = await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { });
        Assert.False(listed.Sites.Single(s => s.Id == box.Site.Id).SigningIn);
        Assert.False(listed.Sites.Single(s => s.Id == box.Site.Id).SignedIn);

        // The port is free again: another sign-in can start (and is cancelled by removing its site).
        var next = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, new SiteSignInRequest { Id = other.Site.Id });
        Assert.True(next.Ok, next.Message);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Remove, new SiteIdRequest { Id = other.Site.Id })).Ok);
        var again = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, new SiteSignInRequest { Id = box.Site.Id });
        Assert.True(again.Ok, again.Message);
        await Call<SiteResultDto>(commands, SitesMessageTypes.SignInCancel, new SiteIdRequest { Id = box.Site.Id });
    }

    /// <summary>
    /// SITES.10: the user's own app reaches rclone through its environment, not its command line (which any program on the
    /// PC can read): rclone's sign-in page still sends the provider that app's id.
    /// </summary>
    [SiteTransfersFact]
    public async Task ASignInWithTheUsersOwnAppSendsItsIdThoughNotOnTheCommandLine()
    {
        var commands = Open(null);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        var box = await Call<SiteResultDto>(commands, SitesMessageTypes.Save,
            new SiteSaveRequest { Name = "Box", Kind = SiteKinds.Dropbox, ClientId = "own-app-id-4471", ClientSecret = "own-secret-9912" });
        Assert.True(box.Ok, box.Message);
        var started = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, new SiteSignInRequest { Id = box.Site!.Id });
        try
        {
            Assert.True(started.Ok, started.Message);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var reply = await http.GetAsync(started.Url);
            Assert.Contains("client_id=own-app-id-4471", reply.Headers.Location?.ToString());
            if (OperatingSystem.IsWindows())
            {
                // No running rclone carries the secret in its command line.
                foreach (var line in CommandLines("rclone.exe")) Assert.DoesNotContain("own-secret-9912", line);
            }
        }
        finally
        {
            await Call<SiteResultDto>(commands, SitesMessageTypes.SignInCancel, new SiteIdRequest { Id = box.Site.Id });
        }
    }

    /// <summary>The command lines of the running processes with that image name, read with WMI's command-line tool.</summary>
    private static List<string> CommandLines(string image)
    {
        var info = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"Get-CimInstance Win32_Process -Filter \"Name='{image}'\" | ForEach-Object {{ $_.CommandLine }}" }) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Assert.Contains(lines, line => line.Contains("authorize", StringComparison.Ordinal));
        return lines;
    }

    [SiteTransfersFact]
    public async Task TurningSitesOffCancelsTheBrowserAuthorization()
    {
        var commands = Open(null);
        var enable = new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone };
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, enable);
        var site = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest { Name = "Box", Kind = SiteKinds.Dropbox });
        var request = new SiteSignInRequest { Id = site.Site!.Id };
        Assert.True((await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, request)).Ok);
        Assert.False((await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = false })).Enabled);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, enable);
        var listed = Assert.Single((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { })).Sites);
        Assert.False(listed.SigningIn);
        Assert.False(listed.SignedIn);
        var next = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, request);
        Assert.True(next.Ok, next.Message);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.SignInCancel, new SiteIdRequest { Id = request.Id })).Ok);
    }

    [SiteTransfersFact]
    public async Task APastedTokenSignsInAndANewClientIdAsksForANewSignIn()
    {
        var commands = Open(null);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest { Name = "Box", Kind = SiteKinds.Dropbox });
        var id = saved.Site!.Id;

        var junk = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, new SiteSignInRequest { Id = id, Token = "not a token" });
        Assert.False(junk.Ok);
        var noAccess = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, new SiteSignInRequest { Id = id, Token = """{"token_type":"bearer"}""" });
        Assert.False(noAccess.Ok);

        // What rclone authorize prints, pasted whole.
        var pasted = $"Paste the following into your remote machine --->\n{PastedToken}\n<---End paste\n";
        var signed = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn, new SiteSignInRequest { Id = id, Token = pasted });
        Assert.True(signed.Ok, signed.Message);
        Assert.True(signed.Site!.SignedIn);
        // The token is sealed in the file.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(State, "sites"), "*.json", SearchOption.AllDirectories))
            Assert.DoesNotContain("made-up-access-0009", await File.ReadAllTextAsync(file));

        // An edit that keeps the app registration keeps the sign-in; a new client ID is a new grant.
        var renamed = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest { Id = id, Name = "Box 2", Kind = SiteKinds.Dropbox });
        Assert.True(renamed.Site!.SignedIn);
        var ownApp = await Call<SiteResultDto>(commands, SitesMessageTypes.Save,
            new SiteSaveRequest { Id = id, Name = "Box 2", Kind = SiteKinds.Dropbox, ClientId = "my-app-id", ClientSecret = "my-secret-1" });
        Assert.True(ownApp.Ok, ownApp.Message);
        Assert.False(ownApp.Site!.SignedIn);
        Assert.Equal("my-app-id", ownApp.Site.ClientId);
        Assert.True(ownApp.Site.HasClientSecret);
        // A secret belongs to its client ID: another ID without one has none.
        var otherApp = await Call<SiteResultDto>(commands, SitesMessageTypes.Save,
            new SiteSaveRequest { Id = id, Name = "Box 2", Kind = SiteKinds.Dropbox, ClientId = "other-app-id" });
        Assert.False(otherApp.Site!.HasClientSecret);

        // A password on a sign-in kind, or a client ID elsewhere, is refused.
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.Save,
            new SiteSaveRequest { Name = "X", Kind = SiteKinds.Drive, Password = "nope" })).Ok);
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.Save,
            new SiteSaveRequest { Name = "X", Kind = SiteKinds.B2, User = "id", Password = "key", ClientId = "app" })).Ok);
        // B2 needs both keys.
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest { Name = "B", Kind = SiteKinds.B2, User = "id" })).Ok);
        var b2 = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest { Name = "B", Kind = SiteKinds.B2, User = "id", Password = "key-123456" });
        Assert.True(b2.Ok, b2.Message);
        Assert.Equal("api.backblazeb2.com", b2.Site!.Host);
    }

    [Fact]
    public void APastedTokenIsReadFromRclonesOutputOrOnItsOwn()
    {
        Assert.NotNull(SitesCommands.ReadPastedToken(PastedToken));
        Assert.NotNull(SitesCommands.ReadPastedToken($"noise --->{PastedToken}<---End paste noise"));
        Assert.NotNull(SitesCommands.ReadPastedToken(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(PastedToken))));
        Assert.Null(SitesCommands.ReadPastedToken(""));
        Assert.Null(SitesCommands.ReadPastedToken("{}"));
        Assert.Null(SitesCommands.ReadPastedToken("[1,2]"));
        Assert.Null(SitesCommands.ReadPastedToken(new string('a', 40_000)));
    }
}
