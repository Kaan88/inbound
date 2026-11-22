using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// ANDROID.8 (r6): <c>sites.transfer.start</c> with direction <c>copy</c>, from one site to another through the Host's
/// rclone. Two <c>rclone serve sftp</c> servers on loopback ports stand for the two locations, each over a folder of its
/// own, so every byte goes out of one server and into the other.
/// </summary>
public sealed partial class SiteTransfersEndToEndTests
{
    private const string OtherPassword = "copy-target-pass-8830";

    private string OtherServed => Path.Combine(_root, "other-served");

    [SiteTransfersFact]
    public async Task ACopyBetweenTwoSitesArrivesWholeAndConflictsFollowTheBatchsChoice()
    {
        var (commands, from, to) = await OpenTwoSitesAsync(slowSource: false);

        var started = await StartCopyAsync(commands, from, to, "/backup", SiteTransferConflicts.Skip,
            new SiteTransferNameDto { Name = "docs", IsFolder = true }, new SiteTransferNameDto { Name = "one-mb.bin" });
        Assert.Equal(SiteTransferDirections.Copy, started.Direction);
        Assert.Equal((to, "Other", "/backup"), (started.DestinationSiteId, started.DestinationSiteName, started.DestinationFolder));
        Assert.Equal("", started.LocalFolder);
        Assert.Equal(3, started.Files);
        var record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(State, "sites", "transfers", started.Id + ".json")))!.AsObject();
        // An older Host leaves a copy alone rather than taking it for an upload from no folder.
        Assert.Equal(SiteTransferRecord.RemoteCopySchemaVersion, record["schemaVersion"]!.GetValue<int>());

        var done = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.Equal((3, 0, 0), (done.FilesDone, done.FilesSkipped, done.FilesFailed));
        Assert.Equal(done.Bytes, done.BytesDone);
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(OtherServed, "backup", "one-mb.bin"));
        AssertSame(Path.Combine(Served, "pub", "docs", "a.txt"), Path.Combine(OtherServed, "backup", "docs", "a.txt"));
        AssertSame(Path.Combine(Served, "pub", "docs", "deep", "b.txt"), Path.Combine(OtherServed, "backup", "docs", "deep", "b.txt"));
        Assert.Empty(Directory.EnumerateFiles(OtherServed, "*.partial", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Local, "*", SearchOption.AllDirectories)); // nothing staged on this computer

        // Skip leaves the target alone; Keep both makes "name (2)" on the target; Replace writes over a changed file.
        var skipped = await FinishCopyAsync(commands, from, to, SiteTransferConflicts.Skip, "one-mb.bin");
        Assert.Equal((0, 1), (skipped.FilesDone, skipped.FilesSkipped));
        var both = await FinishCopyAsync(commands, from, to, SiteTransferConflicts.KeepBoth, "one-mb.bin");
        Assert.Equal(1, both.FilesDone);
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(OtherServed, "backup", "one-mb (2).bin"));
        var detail = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferGet, new SiteTransferIdRequest { Id = both.Id });
        Assert.Equal("one-mb (2).bin", Assert.Single(detail.Items!).Target);
        await File.WriteAllTextAsync(Path.Combine(OtherServed, "backup", "one-mb.bin"), "changed there");
        var replaced = await FinishCopyAsync(commands, from, to, SiteTransferConflicts.Replace, "one-mb.bin");
        Assert.Equal(1, replaced.FilesDone);
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(OtherServed, "backup", "one-mb.bin"));

        // Neither site's password reaches the log or the saved batches.
        lock (_log) Assert.DoesNotContain(_log, line => line.Contains(Password, StringComparison.Ordinal) || line.Contains(OtherPassword, StringComparison.Ordinal));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(State, "sites", "transfers")))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(OtherPassword, text, StringComparison.Ordinal);
        }
    }

    [SiteTransfersFact]
    public async Task ACopyMakesEmptyFoldersAndRefusesWhatCantBeCopied()
    {
        var (commands, from, to) = await OpenTwoSitesAsync(slowSource: false);
        Directory.CreateDirectory(Path.Combine(Served, "pub", "void"));

        var folders = await FinishCopyAsync(commands, from, to, SiteTransferConflicts.Skip, folder: "void");
        Assert.Equal(SiteTransferStates.Done, folders.State);
        Assert.Equal(1, folders.Folders);
        Assert.True(Directory.Exists(Path.Combine(OtherServed, "backup", "void")));

        async Task<SiteTransferResultDto> Refused(SiteTransferStartRequest request)
        {
            var result = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, request);
            Assert.False(result.Ok);
            return result;
        }

        SiteTransferStartRequest Copy(string destination, string folder, string conflict = SiteTransferConflicts.Skip, string name = "docs") => new()
        {
            SiteId = from, Direction = SiteTransferDirections.Copy, RemoteFolder = "/pub", DestinationSiteId = destination,
            DestinationFolder = folder, Conflict = conflict, Names = [new SiteTransferNameDto { Name = name, IsFolder = name == "docs" }],
        };

        Assert.Contains("into itself", (await Refused(Copy(from, "/pub/docs/deep"))).Message);
        Assert.Contains("into itself", (await Refused(Copy(from, "/pub/docs"))).Message);
        Assert.Contains("the folder the files are in", (await Refused(Copy(from, "/pub", name: "readme.txt"))).Message);
        // Keep both in the same folder would number each file inside the folder, not copy the folder (audit RF-3).
        Assert.Contains("can't be copied into the folder it is in", (await Refused(Copy(from, "/pub", SiteTransferConflicts.KeepBoth))).Message);
        Assert.Contains("no longer exists", (await Refused(Copy("0123456789abcdef0123456789abcdef", "/x"))).Message);
        Assert.Contains("no longer exists", (await Refused(Copy("", "/x"))).Message);

        // The same site, another folder: rclone copies it there. Keep both in the same folder makes a second copy.
        var same = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, Copy(from, "/pub/copied", name: "readme.txt"));
        Assert.True(same.Ok, same.Message);
        Assert.Equal(SiteTransferStates.Done, (await WaitForAsync(commands, same.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State))).State);
        AssertSame(Path.Combine(Served, "pub", "readme.txt"), Path.Combine(Served, "pub", "copied", "readme.txt"));
        var twin = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, Copy(from, "/pub", SiteTransferConflicts.KeepBoth, "readme.txt"));
        Assert.True(twin.Ok, twin.Message);
        Assert.Equal(SiteTransferStates.Done, (await WaitForAsync(commands, twin.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State))).State);
        AssertSame(Path.Combine(Served, "pub", "readme.txt"), Path.Combine(Served, "pub", "readme (2).txt"));
    }

    [SiteTransfersFact]
    public async Task CancelStopsACopyInFlightAndTheTargetIsKeptFromChangesWhileItRuns()
    {
        var (commands, from, to) = await OpenTwoSitesAsync(slowSource: true);
        var started = await StartCopyAsync(commands, from, to, "/backup", SiteTransferConflicts.Skip, new SiteTransferNameDto { Name = "slow.bin" });
        var moving = await WaitForAsync(commands, started.Id, t => t.BytesDone > 0 && t.Speed > 0);
        Assert.Equal(1, moving.FilesRunning);

        // The batch holds both ends: neither the source file nor the target folder is renamed or deleted under it.
        var renameTarget = await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
            new SiteRenameRequest { Id = to, Path = "/", Name = "backup", NewName = "elsewhere" });
        Assert.False(renameTarget.Ok);
        var deleteSource = await Call<SiteChangeResultDto>(commands, SitesMessageTypes.Delete,
            new SiteChangeRequest { Id = from, Path = "/pub", Items = [new SiteChangeItemDto { Name = "slow.bin" }] });
        Assert.False(deleteSource.Ok);

        var canceled = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferCancel, new SiteTransferIdRequest { Id = started.Id });
        Assert.True(canceled.Ok, canceled.Message);
        Assert.Equal(SiteTransferStates.Canceled, canceled.Transfer!.State);
        Assert.Equal(0, canceled.Transfer.FilesRunning);
        // The stopped job left no part of the file at the target, under its own name or a partial one.
        var target = Path.Combine(OtherServed, "backup");
        Assert.True(!Directory.Exists(target) || !Directory.EnumerateFiles(target).Any(), "the target holds a piece of the canceled copy");
    }

    [SiteTransfersFact]
    public async Task RemovingTheTargetSiteCancelsTheCopiesToIt()
    {
        var (commands, from, to) = await OpenTwoSitesAsync(slowSource: true);
        var started = await StartCopyAsync(commands, from, to, "/backup", SiteTransferConflicts.Skip, new SiteTransferNameDto { Name = "slow.bin" });
        await WaitForAsync(commands, started.Id, t => t.BytesDone > 0);
        var removed = await Call<SiteResultDto>(commands, SitesMessageTypes.Remove, new SiteIdRequest { Id = to });
        Assert.True(removed.Ok, removed.Message);
        var canceled = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Canceled, canceled.State);
        Assert.Contains("site was removed", canceled.Message);
    }

    private async Task<(SitesCommands Commands, string From, string To)> OpenTwoSitesAsync(bool slowSource)
    {
        Directory.CreateDirectory(OtherServed);
        var (commands, from) = await OpenSiteAsync(StartServer(slowSource));
        var otherPort = StartServer(false, "sftp", ["--user", User, "--pass", OtherPassword], OtherServed);
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "Other", Kind = SiteKinds.Sftp, Host = "127.0.0.1", Port = otherPort, User = User,
            AuthMethod = SiteAuthMethods.Password, Password = OtherPassword,
        });
        Assert.True(saved.Ok, saved.Message);
        var probe = await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = "127.0.0.1", Port = otherPort });
        Assert.True(probe.Ok, probe.Message);
        var trusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = saved.Site!.Id, Entries = [.. probe.Keys.Select(k => k.Entry)] });
        Assert.True(trusted.Ok, trusted.Message);
        return (commands, from, saved.Site.Id);
    }

    private static async Task<SiteTransferDto> StartCopyAsync(SitesCommands commands, string from, string to, string folder, string conflict,
        params SiteTransferNameDto[] names)
    {
        var started = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = from, Direction = SiteTransferDirections.Copy, RemoteFolder = "/pub", DestinationSiteId = to, DestinationFolder = folder,
            Conflict = conflict, Names = [.. names],
        });
        Assert.True(started.Ok, started.Message);
        return started.Transfer!;
    }

    private static async Task<SiteTransferDto> FinishCopyAsync(SitesCommands commands, string from, string to, string conflict,
        string? file = null, string? folder = null)
    {
        var name = file is not null ? new SiteTransferNameDto { Name = file } : new SiteTransferNameDto { Name = folder!, IsFolder = true };
        var started = await StartCopyAsync(commands, from, to, "/backup", conflict, name);
        return await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State));
    }
}
