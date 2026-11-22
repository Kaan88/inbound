using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.11 R4, <c>sites.testDraft</c> against <c>rclone serve sftp</c> on loopback with a real rclone (INBOUND_RCLONE): the
/// form is tried without being saved, the server's keys are only read until they are accepted, and an edit's saved
/// password goes to its own server and user only. Skipped without rclone.
/// </summary>
public sealed partial class SiteTransfersEndToEndTests
{
    private IEnumerable<string> StoredSiteFiles() => Directory.Exists(Path.Combine(State, "sites"))
        ? Directory.EnumerateFiles(Path.Combine(State, "sites"), "*.json").Where(f => SiteStore.IsId(Path.GetFileNameWithoutExtension(f)))
        : [];

    [SiteTransfersFact]
    public async Task ADraftIsTriedWithoutBeingSavedAndItsKeyIsOnlyReadUntilAccepted()
    {
        var port = StartServer(slow: false);
        var commands = Open();
        Assert.True((await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone })).Enabled);
        SiteSaveRequest Form(string? password) => new()
        {
            Name = "Draft", Kind = SiteKinds.Sftp, Host = "127.0.0.1", Port = port, User = User, AuthMethod = SiteAuthMethods.Password,
            Password = password, RemoteFolder = "/pub",
        };

        // First the server's keys, read with no login.
        var first = await Call<SiteResultDto>(commands, SitesMessageTypes.TestDraft, new SiteTestDraftRequest { Site = Form(Password) });
        Assert.False(first.Ok);
        Assert.Equal(SiteProblems.HostKeyNotAccepted, first.Problem);
        Assert.NotEmpty(first.OfferedKeys!);
        Assert.All(first.OfferedKeys!, key => Assert.StartsWith("SHA256:", key.Fingerprint));

        // Accepted in the form, the keys go with the test: the start folder is listed.
        var keys = first.OfferedKeys!.Select(k => k.Entry).ToList();
        var tried = await Call<SiteResultDto>(commands, SitesMessageTypes.TestDraft, new SiteTestDraftRequest { Site = Form(Password), HostKeys = keys });
        Assert.True(tried.Ok, tried.Message);
        Assert.Equal(4, tried.ItemCount); // readme.txt, one-mb.bin, slow.bin and docs
        Assert.Contains("4 items", tried.Message);
        Assert.DoesNotContain(Password, tried.Message ?? "");

        // A wrong password fails and says nothing of it.
        var wrong = await Call<SiteResultDto>(commands, SitesMessageTypes.TestDraft, new SiteTestDraftRequest { Site = Form("not-the-password"), HostKeys = keys });
        Assert.False(wrong.Ok);
        Assert.DoesNotContain("not-the-password", wrong.Message ?? "");

        // Nothing was written or listed.
        Assert.Empty(StoredSiteFiles());
        Assert.Empty((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { })).Sites);
    }

    [SiteTransfersFact]
    public async Task AnEditsDraftUsesTheSavedPasswordOnlyForItsOwnServerAndUser()
    {
        var port = StartServer(slow: false);
        var other = StartServer(slow: false); // takes the same password: a stored one sent there would work
        var (commands, siteId) = await OpenSiteAsync(port);
        var saved = Assert.Single((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { })).Sites);
        var before = File.ReadAllText(Assert.Single(StoredSiteFiles()));
        SiteSaveRequest Edit(int toPort, string user) => new()
        {
            Id = siteId, Name = "Renamed", Kind = SiteKinds.Sftp, Host = "127.0.0.1", Port = toPort, User = user, AuthMethod = SiteAuthMethods.Password,
            RemoteFolder = "/pub",
        };

        // Same server, port and user: the saved password and trusted key are used, and the edit is not saved.
        var same = await Call<SiteResultDto>(commands, SitesMessageTypes.TestDraft, new SiteTestDraftRequest { Site = Edit(port, User) });
        Assert.True(same.Ok, same.Message);
        Assert.Equal(4, same.ItemCount);

        // Another port, or another user: refused before anything connects, the saved password stays home.
        var moved = await Call<SiteResultDto>(commands, SitesMessageTypes.TestDraft, new SiteTestDraftRequest { Site = Edit(other, User) });
        Assert.False(moved.Ok);
        Assert.Contains("type the password again", moved.Message);
        var otherUser = await Call<SiteResultDto>(commands, SitesMessageTypes.TestDraft, new SiteTestDraftRequest { Site = Edit(port, "someone-else") });
        Assert.False(otherUser.Ok);
        Assert.Contains("type the password again", otherUser.Message);

        // The other server's keys are not the saved ones: they are only read.
        var typed = Edit(other, User);
        typed.Password = Password;
        var newServer = await Call<SiteResultDto>(commands, SitesMessageTypes.TestDraft, new SiteTestDraftRequest { Site = typed });
        Assert.Equal(SiteProblems.HostKeyNotAccepted, newServer.Problem);

        // The saved remote is as it was (the draft above never touched it).
        Assert.Equal(before, File.ReadAllText(Assert.Single(StoredSiteFiles())));
        var after = Assert.Single((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { })).Sites);
        Assert.Equal((saved.Name, saved.Port, saved.User), (after.Name, after.Port, after.User));
    }

    [SiteTransfersFact]
    public async Task ADraftsKeysNeverStandInForTheRemotesOwnTrustedKey()
    {
        var port = StartServer(slow: false);
        var commands = Open();
        Assert.True((await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone })).Enabled);
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "Pinned", Kind = SiteKinds.Sftp, Host = "127.0.0.1", Port = port, User = User, AuthMethod = SiteAuthMethods.Password, Password = Password,
        });
        Assert.True(saved.Ok, saved.Message);
        // Key A: not the server's. Key B: the server's own, as a draft would carry it.
        // The wire form: the type and the 32-byte key, each with its length.
        var blob = new byte[4 + 11 + 4 + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(blob, 11);
        System.Text.Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(blob, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(15), 32);
        System.Security.Cryptography.RandomNumberGenerator.Fill(blob.AsSpan(19));
        var keyA = "ssh-ed25519 " + Convert.ToBase64String(blob);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = saved.Site!.Id, Entries = [keyA] })).Ok);
        var probe = await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = "127.0.0.1", Port = port });
        var keysB = probe.Keys.Select(k => k.Entry).ToList();

        var draft = await Call<SiteResultDto>(commands, SitesMessageTypes.TestDraft, new SiteTestDraftRequest
        {
            Site = new SiteSaveRequest
            {
                Id = saved.Site.Id, Name = "Pinned", Kind = SiteKinds.Sftp, Host = "127.0.0.1", Port = port, User = User, AuthMethod = SiteAuthMethods.Password, RemoteFolder = "/pub",
            },
            HostKeys = keysB,
        });
        // rclone checks the key before it signs in: the saved password never left.
        Assert.False(draft.Ok);
        Assert.True(draft.Problem == SiteProblems.HostKeyChanged, $"{draft.Problem}: {draft.Message}");
        var test = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = saved.Site.Id });
        Assert.Equal(SiteProblems.HostKeyChanged, test.Problem);
    }
}
