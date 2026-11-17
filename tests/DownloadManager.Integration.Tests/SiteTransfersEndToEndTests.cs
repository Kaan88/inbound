using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.4 end to end: <c>sites.transfer.*</c> on a real <see cref="SitesCommands"/> with a real rclone (INBOUND_RCLONE)
/// against <c>rclone serve sftp</c> on a loopback port, which runs on Windows and Linux alike. The server is slowed with
/// <c>--bwlimit</c> where a test needs a file to be in flight. Every file is compared by SHA-256. Skipped without rclone.
/// </summary>
public sealed partial class SiteTransfersEndToEndTests : IAsyncLifetime
{
    private const string User = "inbound";
    private const string Password = "transfer-e2e-pass-5521";
    private const long SlowRate = 1024 * 1024; // the slow server's bytes a second

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "site-transfers-" + Guid.NewGuid().ToString("N"));
    private readonly List<SitesCommands> _commands = [];
    private readonly List<Process> _servers = [];
    private readonly List<string> _log = [];

    private string State => Path.Combine(_root, "state");
    private string Served => Path.Combine(_root, "served");
    private string Local => Path.Combine(_root, "local");

    public static string? Missing => Rclone is null ? "No rclone: set INBOUND_RCLONE to an rclone 1.75.0 or later (it is not in the repository)." : null;

    private static string? Rclone =>
        Environment.GetEnvironmentVariable(RcloneSetup.RcloneVariable) is { Length: > 0 } path && File.Exists(path) ? path : null;

    public async Task InitializeAsync()
    {
        if (Missing is not null) return;
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(Path.Combine(Served, "pub", "docs", "deep"));
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "readme.txt"), "read me");
        await File.WriteAllBytesAsync(Path.Combine(Served, "pub", "one-mb.bin"), RandomNumberGenerator.GetBytes(1024 * 1024));
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "docs", "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "docs", "deep", "b.txt"), "bb");
        // Six seconds at the slow server's rate: long enough to stop it half way.
        await File.WriteAllBytesAsync(Path.Combine(Served, "pub", "slow.bin"), RandomNumberGenerator.GetBytes((int)(6 * SlowRate)));
        PortableSecrets.UseAKeyFileOffWindows();
    }

    public async Task DisposeAsync()
    {
        foreach (var commands in _commands) await commands.DisposeAsync();
        foreach (var server in _servers) Stop(server);
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    [SiteTransfersFact]
    public async Task ADownloadOfAFolderAndAFileArrivesWholeAndConflictsFollowTheBatchsChoice()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        var target = Path.Combine(Local, "dl");

        var started = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId,
            Direction = SiteTransferDirections.Download,
            RemoteFolder = "/pub",
            LocalFolder = target,
            Names = [new SiteTransferNameDto { Name = "docs", IsFolder = true }, new SiteTransferNameDto { Name = "one-mb.bin" }],
        });
        Assert.True(started.Ok, started.Message);
        Assert.Equal(3, started.Transfer!.Files);
        // Written before anything moved.
        Assert.True(File.Exists(Path.Combine(State, "sites", "transfers", started.Transfer.Id + ".json")));

        var done = await WaitForAsync(commands, started.Transfer.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.Equal((3, 0, 0), (done.FilesDone, done.FilesSkipped, done.FilesFailed));
        Assert.Equal(done.Bytes, done.BytesDone);
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(target, "one-mb.bin"));
        AssertSame(Path.Combine(Served, "pub", "docs", "deep", "b.txt"), Path.Combine(target, "docs", "deep", "b.txt"));
        Assert.Empty(Directory.EnumerateFiles(target, "*.partial", SearchOption.AllDirectories));

        // The same again: Skip leaves the files alone; Keep both makes "name (2)".
        var skipped = await StartAndFinishAsync(commands, siteId, target, SiteTransferConflicts.Skip, "one-mb.bin");
        Assert.Equal((0, 1), (skipped.FilesDone, skipped.FilesSkipped));
        var both = await StartAndFinishAsync(commands, siteId, target, SiteTransferConflicts.KeepBoth, "one-mb.bin");
        Assert.Equal(1, both.FilesDone);
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(target, "one-mb (2).bin"));
        var detail = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferGet, new SiteTransferIdRequest { Id = both.Id });
        Assert.Equal("one-mb (2).bin", Assert.Single(detail.Items!).Target);

        // Replace writes over a changed file.
        await File.WriteAllTextAsync(Path.Combine(target, "one-mb.bin"), "changed here");
        var replaced = await StartAndFinishAsync(commands, siteId, target, SiteTransferConflicts.Replace, "one-mb.bin");
        Assert.Equal(1, replaced.FilesDone);
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(target, "one-mb.bin"));

        // Clear removes the finished batches and their files in the state folder, never the downloads.
        var cleared = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferClear, new SiteTransferIdRequest());
        Assert.True(cleared.Ok, cleared.Message);
        Assert.Empty((await Call<SiteTransfersDto>(commands, SitesMessageTypes.TransferList, new { })).Transfers);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(State, "sites", "transfers")));
        Assert.True(File.Exists(Path.Combine(target, "one-mb.bin")));
    }

    [SiteTransfersFact]
    public async Task AnUploadOfAFolderArrivesWholeOnTheServer()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        var source = Path.Combine(Local, "up");
        Directory.CreateDirectory(Path.Combine(source, "photos", "2026"));
        await File.WriteAllBytesAsync(Path.Combine(source, "photos", "2026", "p1.jpg"), RandomNumberGenerator.GetBytes(300_000));
        await File.WriteAllTextAsync(Path.Combine(source, "photos", "list.txt"), "list");

        var started = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId,
            Direction = SiteTransferDirections.Upload,
            RemoteFolder = "/pub/incoming",
            LocalFolder = source,
            Names = [new SiteTransferNameDto { Name = "photos", IsFolder = true }],
        });
        Assert.True(started.Ok, started.Message);
        var done = await WaitForAsync(commands, started.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.Equal(2, done.FilesDone);
        AssertSame(Path.Combine(source, "photos", "2026", "p1.jpg"), Path.Combine(Served, "pub", "incoming", "photos", "2026", "p1.jpg"));
        AssertSame(Path.Combine(source, "photos", "list.txt"), Path.Combine(Served, "pub", "incoming", "photos", "list.txt"));
    }

    [SiteTransfersFact]
    public async Task EmptyFoldersAreMadeAtTheTargetBothWays()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        Directory.CreateDirectory(Path.Combine(Served, "pub", "tree", "empty", "deeper"));
        Directory.CreateDirectory(Path.Combine(Served, "pub", "tree", "full"));
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "tree", "full", "f.txt"), "f");
        // "full-b.txt" sorts between "full" and "full/f.txt", so "full" must not be taken for empty.
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "tree", "full-b.txt"), "b");
        Directory.CreateDirectory(Path.Combine(Served, "pub", "void"));

        var target = Path.Combine(Local, "folders");
        var down = await StartAsync(commands, siteId, target, SiteTransferConflicts.Skip, "tree", "void");
        var downDone = await WaitForAsync(commands, down.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, downDone.State);
        // f.txt, full-b.txt, tree/empty/deeper (the deepest empty one) and void.
        Assert.Equal(4, downDone.Files);
        Assert.Equal(2, downDone.Folders);
        Assert.True(Directory.Exists(Path.Combine(target, "tree", "empty", "deeper")));
        Assert.True(Directory.Exists(Path.Combine(target, "void")));
        AssertSame(Path.Combine(Served, "pub", "tree", "full", "f.txt"), Path.Combine(target, "tree", "full", "f.txt"));
        var detail = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferGet, new SiteTransferIdRequest { Id = down.Id });
        Assert.Equal(["tree/empty/deeper", "void"], detail.Items!.Where(i => i.IsFolder).Select(i => i.Path).Order(StringComparer.Ordinal));

        var source = Path.Combine(Local, "upfolders");
        Directory.CreateDirectory(Path.Combine(source, "album", "blank"));
        await File.WriteAllTextAsync(Path.Combine(source, "album", "a.txt"), "a");
        var up = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId, Direction = SiteTransferDirections.Upload, RemoteFolder = "/pub/incoming2", LocalFolder = source,
            Names = [new SiteTransferNameDto { Name = "album", IsFolder = true }],
        });
        Assert.True(up.Ok, up.Message);
        var upDone = await WaitForAsync(commands, up.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, upDone.State);
        Assert.True(Directory.Exists(Path.Combine(Served, "pub", "incoming2", "album", "blank")));
        AssertSame(Path.Combine(source, "album", "a.txt"), Path.Combine(Served, "pub", "incoming2", "album", "a.txt"));
    }

    [SiteTransfersFact]
    public async Task TheSpeedShareSlowsRclone()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        Assert.False(commands.Transfers.Demand().Active);
        // 256 KiB/s for a 1 MiB file: about three seconds after rclone's first burst, where the server alone takes well
        // under one.
        commands.Transfers.SetSpeedLimit(256 * 1024);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var started = await StartAsync(commands, siteId, Path.Combine(Local, "limited"), SiteTransferConflicts.Skip, "one-mb.bin");
        await WaitForAsync(commands, started.Id, t => t.FilesRunning == 1);
        Assert.True(commands.Transfers.Demand().Active);
        var done = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(60));
        clock.Stop();
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2.5), $"took {clock.Elapsed}");
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(Local, "limited", "one-mb.bin"));
        Assert.False(commands.Transfers.Demand().Active);
    }

    [SiteTransfersFact]
    public async Task PauseStopsTheFileInFlightAndResumeStartsItAgainFromZero()
    {
        var port = StartServer(slow: true);
        var sleep = new RecordingSleep();
        var (commands, siteId) = await OpenSiteAsync(port, sleep: sleep);
        var target = Path.Combine(Local, "paused");

        var started = await StartAsync(commands, siteId, target, SiteTransferConflicts.Skip, "slow.bin");
        await WaitForAsync(commands, started.Id, t => t.BytesDone > 0);
        Assert.True(sleep.Held); // a running transfer keeps the PC awake, as a download does
        Assert.Single(Directory.EnumerateFiles(target, "slow.bin.*.partial"));

        var paused = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferPause, new SiteTransferIdRequest { Id = started.Id });
        Assert.True(paused.Ok, paused.Message);
        Assert.Equal(SiteTransferStates.Paused, paused.Transfer!.State);
        Assert.Equal(0, paused.Transfer.FilesRunning);
        Assert.False(sleep.Held);
        // rclone removed its partial file when the job was stopped; nothing is left under the final name either.
        Assert.Empty(Directory.EnumerateFiles(target));

        var resumed = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferResume, new SiteTransferIdRequest { Id = started.Id });
        Assert.True(resumed.Ok, resumed.Message);
        var done = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(60));
        Assert.Equal(SiteTransferStates.Done, done.State);
        AssertSame(Path.Combine(Served, "pub", "slow.bin"), Path.Combine(target, "slow.bin"));
        Assert.False(sleep.Held);
    }

    [SiteTransfersFact]
    public async Task ABatchCutByAHostCrashGoesOnAtTheNextStartWithoutItsPartialFile()
    {
        var port = StartServer(slow: true);
        var (commands, siteId) = await OpenSiteAsync(port);
        var target = Path.Combine(Local, "crash");

        var started = await StartAsync(commands, siteId, target, SiteTransferConflicts.Skip, "slow.bin", "readme.txt");
        await WaitForAsync(commands, started.Id, t => t.BytesDone > 0 && t.FilesRunning > 0);

        // The Host stops (a kill looks the same on disk: which file was moving is never saved), and a partial file of
        // the cut copy is left behind, as rclone would leave it if it was killed too.
        await commands.DisposeAsync();
        _commands.Remove(commands);
        var file = Path.Combine(State, "sites", "transfers", started.Id + ".json");
        var record = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        Assert.Equal(SiteTransferStates.Running, record["state"]!.GetValue<string>());
        var stray = Path.Combine(target, "slow.bin.0a1b2c3d.partial");
        var mine = Path.Combine(target, "slow.bin.old.partial"); // the user's own file, not rclone's
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(stray, "half a file");
        await File.WriteAllTextAsync(mine, "keep me");

        var again = Open();
        var done = await WaitForAsync(again, started.Id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(60));
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(mine));
        Assert.Equal([mine], Directory.EnumerateFiles(target, "*.partial"));
        AssertSame(Path.Combine(Served, "pub", "slow.bin"), Path.Combine(target, "slow.bin"));
        AssertSame(Path.Combine(Served, "pub", "readme.txt"), Path.Combine(target, "readme.txt"));
        lock (_log) Assert.Contains(_log, line => line.Contains("partial file(s) left by a stop", StringComparison.Ordinal));
    }

    [SiteTransfersFact]
    public async Task TurningSitesOffPausesARunningBatchAndAMissingFileFailsWithoutStoppingTheOthers()
    {
        var port = StartServer(slow: true);
        var (commands, siteId) = await OpenSiteAsync(port);
        var target = Path.Combine(Local, "off");
        var started = await StartAsync(commands, siteId, target, SiteTransferConflicts.Skip, "slow.bin");
        await WaitForAsync(commands, started.Id, t => t.FilesRunning > 0);

        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = false });
        var list = await Call<SiteTransfersDto>(commands, SitesMessageTypes.TransferList, new { });
        var paused = Assert.Single(list.Transfers);
        Assert.Equal(SiteTransferStates.Paused, paused.State);
        Assert.Equal("Paused when Remote files was turned off.", paused.Message);
        var refused = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferResume, new SiteTransferIdRequest { Id = started.Id });
        Assert.Equal(SiteProblems.Off, refused.Problem);

        // On again, one file at a time; a batch whose second file vanishes from the server while the first moves: that
        // file fails at once (no retries for a file that is gone), and the first still arrives.
        var on = await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone, MaxTransfers = 1 });
        Assert.Equal(1, on.MaxTransfers);
        await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferCancel, new SiteTransferIdRequest { Id = started.Id });
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "gone.txt"), "soon gone");
        var mixed = await StartAsync(commands, siteId, Path.Combine(Local, "mixed"), SiteTransferConflicts.Skip, "slow.bin", "gone.txt");
        await WaitForAsync(commands, mixed.Id, t => t.FilesRunning == 1);
        File.Delete(Path.Combine(Served, "pub", "gone.txt"));
        var finished = await WaitForAsync(commands, mixed.Id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(60));
        Assert.Equal(SiteTransferStates.Failed, finished.State);
        Assert.Equal((1, 1), (finished.FilesDone, finished.FilesFailed));
        var detail = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferGet, new SiteTransferIdRequest { Id = mixed.Id });
        var failed = detail.Items!.Single(i => i.State == SiteTransferItemStates.Failed);
        Assert.Equal(("gone.txt", 1, "The file is no longer on the server."), (failed.Path, failed.Tries, failed.Error));
        AssertSame(Path.Combine(Served, "pub", "slow.bin"), Path.Combine(Local, "mixed", "slow.bin"));

        // Retry tries the failed file again: it is still gone, so it fails again.
        var retried = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferRetry, new SiteTransferIdRequest { Id = mixed.Id });
        Assert.True(retried.Ok, retried.Message);
        var again = await WaitForAsync(commands, mixed.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal((SiteTransferStates.Failed, 1, 1), (again.State, again.FilesDone, again.FilesFailed));
    }

    [SiteTransfersFact]
    public async Task ARefusedSignInStopsTheBatchAtOnceWithoutSpendingTheOtherFilesTries()
    {
        var port = StartServer(slow: true);
        var (commands, siteId) = await OpenSiteAsync(port, maxTransfers: 1);
        var started = await StartAsync(commands, siteId, Path.Combine(Local, "signin"), SiteTransferConflicts.Skip, "slow.bin", "readme.txt");
        await WaitForAsync(commands, started.Id, t => t.FilesRunning == 1);

        // The password changes while the first file moves; the next file's sign-in is refused.
        var site = (await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { })).Sites.Single();
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Id = siteId, Name = site.Name, Kind = site.Kind, Host = site.Host, Port = site.Port, User = site.User,
            AuthMethod = SiteAuthMethods.Password, Password = "not-the-password",
        });
        Assert.True(saved.Ok, saved.Message);

        var stopped = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(60));
        Assert.Equal(SiteTransferStates.Failed, stopped.State);
        Assert.Equal("The server did not accept the sign-in. Check the user name, and the password or key, in Edit remote.", stopped.Message);
        var detail = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferGet, new SiteTransferIdRequest { Id = started.Id });
        var readme = detail.Items!.Single(i => i.Path == "readme.txt");
        Assert.Equal((SiteTransferItemStates.Waiting, 0), (readme.State, readme.Tries));
        // The password is in no reply and no log line.
        lock (_log) Assert.DoesNotContain(_log, line => line.Contains("not-the-password", StringComparison.Ordinal) || line.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain("not-the-password", await File.ReadAllTextAsync(Path.Combine(State, "sites", "transfers", started.Id + ".json")));

        // With the right password back, Resume goes on with the files that were waiting.
        saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Id = siteId, Name = site.Name, Kind = site.Kind, Host = site.Host, Port = site.Port, User = site.User,
            AuthMethod = SiteAuthMethods.Password, Password = Password,
        });
        Assert.True(saved.Ok, saved.Message);
        var resumed = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferResume, new SiteTransferIdRequest { Id = started.Id });
        Assert.True(resumed.Ok, resumed.Message);
        var done = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(60));
        Assert.Equal(SiteTransferStates.Done, done.State);
        AssertSame(Path.Combine(Served, "pub", "readme.txt"), Path.Combine(Local, "signin", "readme.txt"));
    }

    [SiteTransfersFact]
    public async Task AServerThatGoesAwayMakesTheBatchWaitThenStopWithoutFailingItsFiles()
    {
        var port = StartServer(slow: true);
        var (commands, siteId) = await OpenSiteAsync(port, maxTransfers: 1);
        var started = await StartAsync(commands, siteId, Path.Combine(Local, "away"), SiteTransferConflicts.Skip, "slow.bin", "readme.txt");
        await WaitForAsync(commands, started.Id, t => t.FilesRunning == 1 && t.BytesDone > 0);

        foreach (var server in _servers) Stop(server);
        _servers.Clear();

        var stopped = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(90));
        Assert.Equal(SiteTransferStates.Failed, stopped.State);
        Assert.Contains("The transfer stopped after 5 failed connections in a row; Retry goes on.", stopped.Message);
        Assert.Equal(0, stopped.FilesFailed);
        var detail = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferGet, new SiteTransferIdRequest { Id = started.Id });
        // The file cut by the server going away spent one try at most; the connection failures after it spent none.
        Assert.All(detail.Items!, item => Assert.True(item.Tries <= 1, $"{item.Path}: {item.Tries} tries"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Local, "away"), "*.partial"));

        // Retry goes on with the waiting files (the server is still away, so they wait again).
        var retried = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferRetry, new SiteTransferIdRequest { Id = started.Id });
        Assert.True(retried.Ok, retried.Message);
        Assert.Equal(SiteTransferStates.Running, retried.Transfer!.State);
        // Canceled, it can't be retried: Retry would start the files nobody asked for any more.
        await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferCancel, new SiteTransferIdRequest { Id = started.Id });
        var again = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferRetry, new SiteTransferIdRequest { Id = started.Id });
        Assert.False(again.Ok);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private sealed class RecordingSleep : DownloadManager.Core.ISleepPrevention
    {
        public volatile bool Held;
        public void SetActive(bool active) => Held = active;
        public void Dispose() { }
    }

    private SitesCommands Open(DownloadManager.Core.ISleepPrevention? sleep = null)
    {
        var commands = new SitesCommands(State, line => { lock (_log) _log.Add(line); }, findRclone: _ => Rclone,
            transferRetryDelay: TimeSpan.FromMilliseconds(100), sleepPrevention: sleep);
        _commands.Add(commands);
        return commands;
    }

    private async Task<(SitesCommands Commands, string SiteId)> OpenSiteAsync(int port, int? maxTransfers = null,
        DownloadManager.Core.ISleepPrevention? sleep = null)
    {
        var commands = Open(sleep);
        var status = await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled,
            new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone, MaxTransfers = maxTransfers });
        Assert.True(status.Enabled);
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "Loopback",
            Kind = SiteKinds.Sftp,
            Host = "127.0.0.1",
            Port = port,
            User = User,
            AuthMethod = SiteAuthMethods.Password,
            Password = Password,
        });
        Assert.True(saved.Ok, saved.Message);
        var probe = await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = "127.0.0.1", Port = port });
        Assert.True(probe.Ok, probe.Message);
        var trusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = saved.Site!.Id, Entries = [.. probe.Keys.Select(k => k.Entry)] });
        Assert.True(trusted.Ok, trusted.Message);
        return (commands, saved.Site.Id);
    }

    private static async Task<SiteTransferDto> StartAsync(SitesCommands commands, string siteId, string target, string conflict, params string[] names)
    {
        var started = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId,
            Direction = SiteTransferDirections.Download,
            RemoteFolder = "/pub",
            LocalFolder = target,
            Conflict = conflict,
            Names = [.. names.Select(n => new SiteTransferNameDto { Name = n })],
        });
        Assert.True(started.Ok, started.Message);
        return started.Transfer!;
    }

    private static async Task<SiteTransferDto> StartAndFinishAsync(SitesCommands commands, string siteId, string target, string conflict, params string[] names)
    {
        var started = await StartAsync(commands, siteId, target, conflict, names);
        return await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State));
    }

    private static async Task<SiteTransferDto> WaitForAsync(SitesCommands commands, string id, Func<SiteTransferDto, bool> condition, TimeSpan? limit = null)
    {
        var until = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(30));
        SiteTransferDto? last = null;
        while (DateTime.UtcNow < until)
        {
            var list = await Call<SiteTransfersDto>(commands, SitesMessageTypes.TransferList, new { });
            last = list.Transfers.SingleOrDefault(t => t.Id == id);
            if (last is not null && condition(last)) return last;
            await Task.Delay(100);
        }

        throw new TimeoutException($"The transfer did not get there; last state {last?.State}, {last?.FilesDone} done, {last?.FilesFailed} failed, {last?.BytesDone} bytes.");
    }

    private static void AssertSame(string expected, string actual)
    {
        Assert.True(File.Exists(actual), $"{actual} is missing.");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expected))), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actual))));
    }

    private int StartServer(bool slow) => StartServer(slow, "sftp", ["--user", User, "--pass", Password]);

    /// <summary>
    /// <c>rclone serve &lt;protocol&gt;</c> over <see cref="Served"/> on a free loopback port (SITES.6: <c>ftp</c>, <c>webdav</c>
    /// and <c>s3</c> too), with <paramref name="login"/> as the server's own sign-in options.
    /// </summary>
    private int StartServer(bool slow, string protocol, string[] login)
    {
        var port = FreePort();
        var start = new ProcessStartInfo(Rclone!)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "serve", protocol, Served, "--addr", $"127.0.0.1:{port}" }.Concat(login).Concat(new[] {
                     "--cache-dir", Path.Combine(_root, "server-cache"), "--config", "",
                     // No folder cache: files the test adds or removes are seen at once, as on a real server.
                     "--dir-cache-time", "0s" }))
            start.ArgumentList.Add(arg);
        if (slow)
        {
            start.ArgumentList.Add("--bwlimit");
            start.ArgumentList.Add("1M"); // SlowRate; a bare number would be KiB a second
        }

        var server = Process.Start(start)!;
        server.OutputDataReceived += (_, _) => { };
        server.ErrorDataReceived += (_, _) => { };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        _servers.Add(server);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                return port;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }

        throw new InvalidOperationException($"rclone serve {protocol} did not start.");
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        process.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<T> Call<T>(SitesCommands commands, string type, object payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return (await commands.HandleAsync(IpcMessage.Create(type, payload), timeout.Token)).GetPayload<T>();
    }
}

public sealed class SiteTransfersFactAttribute : FactAttribute
{
    public SiteTransfersFactAttribute()
    {
        if (SiteTransfersEndToEndTests.Missing is not null) Skip = SiteTransfersEndToEndTests.Missing;
    }
}
