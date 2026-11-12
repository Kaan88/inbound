using System.Text.Json;
using DownloadManager.Cli;
using DownloadManager.Contracts;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// <c>dm add</c>, <c>add-batch</c>, <c>schedule</c>, <c>profile</c>, <c>update</c>, <c>export</c> and <c>import</c> through the
/// real CLI and Host (audit 4: L11, L13, L18, L27, M12, N5, N6; audit 5: C1-C3).
/// </summary>
[Trait("Platform", "Windows")]
public sealed class CliCommandTests
{
    private const string Api = "https://example.invalid/v1/api/";

    [Fact]
    public async Task AddTakesARelativeFolderFromWhereItRunsTheDefaultQueueAndAValueThatStartsWithTwoDashes()
    {
        await using var host = await SandboxHost.StartAsync(Api);
        await using var connection = await host.ConnectAsync();

        // A second queue made the default: "no --queue" must land there, not in queue 0 (L18).
        var settings = await SettingsOf(connection);
        settings.Queues.Add(new QueueDto { Id = 7, Name = "Second", MaxConcurrentDownloads = 1, IsRunning = true, Order = settings.Queues.Count });
        settings.DefaultQueueId = 7;
        Assert.True((await connection.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings })).Ok);

        // The CLI runs in the sandbox's root, so "rel" is a folder there and not in the Host's folder (L11).
        var add = await host.RunCliAsync(null, "add", "https://example.invalid/rel.bin", "--dest", "rel", "--start", "later",
            "--description=--draft");
        Assert.True(add.ExitCode == 0, add.All);
        var job = Assert.Single((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs);
        Assert.Equal(Path.Combine(host.Root, "rel"), job.DestinationDirectory.TrimEnd('\\'), ignoreCase: true);
        Assert.Equal(7, job.QueueId);
        // N6: a value that starts with "--" is given with "=".
        Assert.Equal("--draft", job.Description);

        // Without "=" it is still read as a missing value, and the error says how to give one.
        var bare = await host.RunCliAsync(null, "add", "https://example.invalid/b.bin", "--description", "--draft");
        Assert.Equal(2, bare.ExitCode);
        Assert.Contains("--description=", bare.Error, StringComparison.Ordinal);

        // A value that looks like a flag is never read as one: "--dest" here is the description, and the folder is the
        // --dest given after it, not the text "--dest".
        var lookalike = await host.RunCliAsync(null, "add", "https://example.invalid/c.bin", "--description=--dest", "--dest", "rel2",
            "--start", "later");
        Assert.True(lookalike.ExitCode == 0, lookalike.All);
        var second = (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs.Single(j => j.Id != job.Id);
        Assert.Equal(Path.Combine(host.Root, "rel2"), second.DestinationDirectory.TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
        Assert.Equal("--dest", second.Description);

        // A --name=value with no such flag is refused, not dropped without a word, and its value is not repeated.
        var mistyped = await host.RunCliAsync(null, "add", "https://example.invalid/d.bin", "--destination=secret-folder");
        Assert.Equal(2, mistyped.ExitCode);
        Assert.Contains("--destination", mistyped.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-folder", mistyped.All, StringComparison.Ordinal);
        Assert.Equal(2, (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs.Count);
    }

    [Fact]
    public async Task AddBatchWaitFailsWhenAUrlWasRefusedAndStatusListAndUnknownCommandsSayItPlainly()
    {
        await using var host = await SandboxHost.StartAsync(Api);
        await using var connection = await host.ConnectAsync();

        // One address the Host takes and one it refuses (a scheme no download can have); "--start later" ends the wait.
        var batch = await host.RunCliAsync(null, "add-batch", "[https://example.invalid/ok.bin,gopher://example.invalid/no.bin]",
            "--dest", host.Destination, "--start", "later", "--wait");
        Assert.Single((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs);
        Assert.True(batch.ExitCode == 1, batch.All);

        var list = await host.RunCliAsync(null, "list");
        Assert.True(list.ExitCode == 0, list.All);
        Assert.DoesNotContain(" 0 B", list.Output, StringComparison.Ordinal);

        // C3: not an id is said to be one that isn't there, not answered with the usage line.
        var status = await host.RunCliAsync(null, "status", "zzz");
        Assert.Equal(1, status.ExitCode);
        Assert.Contains("No download with the ID 'zzz'", status.Error, StringComparison.Ordinal);

        // C1: the error and where the help is, not the whole help.
        var unknown = await host.RunCliAsync(null, "nonsense");
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("unknown command 'nonsense'", unknown.Error, StringComparison.Ordinal);
        Assert.Contains("--help", unknown.Error, StringComparison.Ordinal);
        Assert.Empty(unknown.Output);

        // CUT.1: the archive and TorBox (remote) commands are gone and get the same short error, before their flags are
        // read (a "--provider=x" is not reported as an unknown flag). dm torrent stays (CUT.2).
        foreach (var removed in new[]
                 {
                     new[] { "archive", "extract", "x.rar", "--password-stdin" },
                     ["remote", "status", "--provider=torbox"],
                 })
        {
            var gone = await host.RunCliAsync(null, removed);
            Assert.Equal(2, gone.ExitCode);
            Assert.Equal($"unknown command '{removed[0]}'. Run \"DownloadManager.Cli --help\" for the commands.", gone.Error.Trim());
            Assert.Empty(gone.Output);
        }

        var help = await host.RunCliAsync(null, "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("dm torrent add", help.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("dm archive", help.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("dm remote", help.Output, StringComparison.Ordinal);
        Assert.Contains("dm settings", help.Output, StringComparison.Ordinal);

        // C3: a setting saved is said in one line, not followed by every setting.
        var set = await host.RunCliAsync(null, "settings", "set", "maxconcurrent", "3");
        Assert.True(set.ExitCode == 0, set.All);
        Assert.DoesNotContain("default dir:", set.Output, StringComparison.Ordinal);
        Assert.Equal(3, (await SettingsOf(connection)).MaxConcurrentDownloads);
    }

    [Fact]
    public async Task ScheduleProfileAndUpdateWorkThroughTheHost()
    {
        await using var host = await SandboxHost.StartAsync(Api);
        await using var connection = await host.ConnectAsync();
        var queue = (await SettingsOf(connection)).Queues[0];

        var added = await host.RunCliAsync(null, "schedule", "add", queue.Id.ToString(), "21:30", "--days", "Mon,Fri", "--name", "Evening");
        Assert.True(added.ExitCode == 0, added.All);
        var schedule = Assert.Single((await SettingsOf(connection)).Schedules);
        Assert.Equal(("Evening", "21:30", "Mon,Fri"), (schedule.Name, schedule.StartTime, schedule.Days));
        Assert.Contains("Evening", (await host.RunCliAsync(null, "schedule", "list")).Output, StringComparison.Ordinal);
        var removed = await host.RunCliAsync(null, "schedule", "remove", schedule.Id.ToString("N")[..8]);
        Assert.True(removed.ExitCode == 0, removed.All);
        Assert.Empty((await SettingsOf(connection)).Schedules);

        var profile = await host.RunCliAsync(null, "profile", "add", "*.example.com", "4");
        Assert.True(profile.ExitCode == 0, profile.All);
        Assert.Equal(("*.example.com", 4), (Assert.Single((await SettingsOf(connection)).ConnectionProfiles) is var p ? (p.Pattern, p.MaxConnections) : default));
        Assert.Contains("*.example.com", (await host.RunCliAsync(null, "profile", "list")).Output, StringComparison.Ordinal);
        Assert.Equal(2, (await host.RunCliAsync(null, "profile", "add", "*.example.com", "40")).ExitCode);
        Assert.True((await host.RunCliAsync(null, "profile", "remove", "*.example.com")).ExitCode == 0);
        Assert.Empty((await SettingsOf(connection)).ConnectionProfiles);
        Assert.Equal(1, (await host.RunCliAsync(null, "profile", "remove", "*.example.com")).ExitCode);

        // A build that Setup did not install has no updates to look for; it says so and doesn't fail.
        var update = await host.RunCliAsync(null, "update", "check", "--json");
        var check = JsonSerializer.Deserialize<UpdateCheckResult>(update.Output, IpcJson.Options)!;
        Assert.False(string.IsNullOrWhiteSpace(check.Status), update.All);
        Assert.Equal(check.Status is "updateAvailable" or "upToDate" or "notInstalled" ? 0 : 1, update.ExitCode);
    }

    [Fact]
    public async Task ExportKeepsATorrentAndAnImportBringsItBackAndAHostErrorIsNeverReadAsNoTorrents()
    {
        await using var host = await SandboxHost.StartAsync(Api, sandbox =>
        {
            var worker = Path.Combine(sandbox.Root, TorrentSetup.WorkerFileName);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), worker);
            sandbox.ExtraEnvironment[TorrentSetup.WorkerVariable] = worker;
        });
        await using var connection = await host.ConnectAsync();
        var settings = await SettingsOf(connection);
        settings.TorrentsEnabled = true;
        Assert.True((await connection.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings })).Ok);

        var file = Path.Combine(host.Root, "film.torrent");
        var (bytes, hash) = HostCommandDispatcherTests.SingleFileTorrent("film.mkv", 100_000);
        await File.WriteAllBytesAsync(file, bytes);
        // torrent reads its own arguments, and takes --dest=DIR too (it used to drop it and use the default folder).
        var add = await host.RunCliAsync(null, "torrent", "add", file, "--dest=" + host.Destination, "--later", "--json");
        Assert.True(add.ExitCode == 0, add.All);
        var added = JsonSerializer.Deserialize<TorrentAddResult>(add.Output, IpcJson.Options)!.Job!;
        Assert.Equal(host.Destination.TrimEnd(Path.DirectorySeparatorChar), added.DestinationDirectory.TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
        var id = added.Id;

        // The export has the .torrent itself, so the import adds it as it was.
        var exportFile = Path.Combine(host.Root, "with-torrent.json");
        var exported = await host.RunCliAsync(null, "export", exportFile);
        Assert.True(exported.ExitCode == 0, exported.All);
        var entry = Assert.Single(JobListFile.Deserialize(await File.ReadAllTextAsync(exportFile)));
        Assert.Equal(hash, entry.Torrent!.InfoHash, ignoreCase: true);
        Assert.Equal(bytes, Convert.FromBase64String(entry.Torrent.Data!));

        // M12/N5: the Host can't read the saved .torrent (held open here), so it answers with an error. The export stops
        // with its words and leaves the file as it was, instead of writing it without the torrent.
        var metadata = Directory.GetFiles(host.DataDirectory, $"{id:N}.torrent", SearchOption.AllDirectories).Single();
        var refusedFile = Path.Combine(host.Root, "refused.json");
        await File.WriteAllTextAsync(refusedFile, "old");
        await using (new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var refused = await host.RunCliAsync(null, "export", refusedFile);
            Assert.Equal(1, refused.ExitCode);
            Assert.Contains("export failed:", refused.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("exported", refused.Output, StringComparison.Ordinal);
        }

        Assert.Equal("old", await File.ReadAllTextAsync(refusedFile));

        var removed = await host.RunCliAsync(null, "remove", id.ToString());
        Assert.True(removed.ExitCode == 0, removed.All);
        await WaitForAsync(async () => (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs.Count == 0);

        var imported = await host.RunCliAsync(null, "import", exportFile, "--start", "later");
        Assert.True(imported.ExitCode == 0, imported.All);
        var back = Assert.Single((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs);
        Assert.Equal(hash, back.Torrent!.InfoHash, ignoreCase: true);

        // An import the Host fails with an error (here another torrent to a folder no path can have, which the Host answers
        // with host.actionResult) is reported as failed in its words, not counted as torrents it doesn't download.
        var (otherBytes, otherHash) = HostCommandDispatcherTests.SingleFileTorrent("other.mkv", 50_000);
        var broken = Path.Combine(host.Root, "broken.json");
        entry.DestinationDirectory = host.Destination + "\0x";
        entry.Torrent.InfoHash = otherHash;
        entry.Torrent.Data = Convert.ToBase64String(otherBytes);
        await File.WriteAllTextAsync(broken, JsonSerializer.Serialize(new[] { entry }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var failed = await host.RunCliAsync(null, "import", broken, "--start", "later");
        Assert.Equal(1, failed.ExitCode);
        Assert.DoesNotContain("does not download torrents", failed.All, StringComparison.Ordinal);
        Assert.Contains("1 failed: ", failed.Output, StringComparison.Ordinal);
        Assert.Single((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(100);
        }
    }

    private static async Task<SettingsDto> SettingsOf(HostConnection connection)
        => (await connection.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings;
}

/// <summary>Pure functions of the CLI: argument reading, reply timeouts and what it prints (audit 4 N3, N6, L15; audit 5 C2).</summary>
public sealed class CliTextTests
{
    [Fact]
    public void AFlagGivenWithEqualsTakesAValueThatStartsWithTwoDashes()
    {
        string[] args = ["add", "https://example.invalid/a.bin", "--description=--draft", "--name=", "--wait"];
        Assert.Equal("--name=", CliArgs.MissingValue(args));

        string[] given = ["add", "https://example.invalid/a.bin", "--description=--draft", "--wait", "--unknown=x"];
        Assert.Null(CliArgs.MissingValue(given));
        var expanded = CliArgs.Expand(given);
        Assert.Equal("--draft", DownloadManager.Cli.Program.Option(expanded, "--description"));
        Assert.Equal(["https://example.invalid/a.bin"], CliArgs.Positional(expanded));
        // Only a flag that takes a value is split; any other --name=value is refused by its name alone.
        Assert.Contains("--unknown=x", expanded);
        Assert.Equal("--unknown", CliArgs.UnknownJoined(given, CliArgs.ValueFlags));
    }

    [Fact]
    public void AValueThatLooksLikeAFlagIsNeverReadAsOne()
    {
        // --description=--wait: the description is "--wait", and the command does not wait.
        var wait = CliArgs.Expand(["add", "https://example.invalid/a.bin", "--description=--wait", "--start", "later"]);
        Assert.False(CliArgs.HasFlag(wait, "--wait"));
        Assert.Equal("--wait", DownloadManager.Cli.Program.Option(wait, "--description"));
        Assert.True(CliArgs.HasFlag(CliArgs.Expand(["add", "https://example.invalid/a.bin", "--wait"]), "--wait"));
        Assert.False(CliArgs.HasFlag(CliArgs.Expand(["add", "https://example.invalid/a.bin", "--name=--direct"]), "--direct"));

        // --description=--dest --dest D:\x: the folder is D:\x, not "--dest".
        var dest = CliArgs.Expand(["add", "https://example.invalid/a.bin", "--description=--dest", "--dest", @"D:\x"]);
        Assert.Equal(@"D:\x", DownloadManager.Cli.Program.Option(dest, "--dest"));
        Assert.Equal(["https://example.invalid/a.bin"], CliArgs.Positional(dest));
        // A value is never split again, even one that looks like --flag=value.
        Assert.Equal(["add", "u", "--description", "--name=x"], CliArgs.Expand(["add", "u", "--description=--name=x"]));
        Assert.Null(CliArgs.UnknownJoined(["add", "u", "--description=--name=x"], CliArgs.ValueFlags));
        Assert.Null(CliArgs.UnknownJoined(["add", "u", "--header", "X-A=b"], CliArgs.ValueFlags));
    }

    [Fact]
    public void TorrentTakesFlagEqualsValueToo()
    {
        var torrent = CliArgs.ValueFlagsOf("torrent");
        string[] add = ["torrent", "add", "x.torrent", @"--dest=D:\dl", "--later"];
        Assert.Null(CliArgs.MissingValue(add, torrent, nextMustBeValue: false));
        Assert.Null(CliArgs.UnknownJoined(add, torrent));
        Assert.Equal(["torrent", "add", "x.torrent", "--dest", @"D:\dl", "--later"], CliArgs.Expand(add, torrent));
        Assert.Equal("--dest=", CliArgs.MissingValue(["torrent", "add", "x.torrent", "--dest="], torrent, nextMustBeValue: false));
        // A bare flag followed by another stays torrent's own business (it has no value), not a missing-value error.
        Assert.Null(CliArgs.MissingValue(["torrent", "add", "x.torrent", "--dest", "--later"], torrent, nextMustBeValue: false));
        // torrent never reads a value that starts with "--", so that is refused rather than dropped.
        Assert.Equal("--dest", CliArgs.JoinedValueStartingWithDashes(["torrent", "add", "x", "--dest=--x"], torrent));
        Assert.Equal("--destination", CliArgs.UnknownJoined(["torrent", "add", "x", @"--destination=D:\dl"], torrent));
        // The cut commands read nothing of their own any more.
        Assert.Same(CliArgs.ValueFlags, CliArgs.ValueFlagsOf("archive"));
        Assert.Same(CliArgs.ValueFlags, CliArgs.ValueFlagsOf("remote"));
    }

    [Fact]
    public void TheListShowsTheWindowsStateWordsAndNoSizeWhenNoneIsKnown()
    {
        Assert.Equal("Retrying", DownloadManager.Cli.Program.StateText("retryWaiting"));
        Assert.Equal("Downloading", DownloadManager.Cli.Program.StateText("downloading"));
        Assert.Equal("somethingNew", DownloadManager.Cli.Program.StateText("somethingNew"));
        Assert.Equal(string.Empty, DownloadManager.Cli.Program.ListSizeText(new JobDto { ReceivedBytes = 0 }));
        Assert.Equal(DownloadManager.Core.FormatUtil.Bytes(1024), DownloadManager.Cli.Program.ListSizeText(new JobDto { ReceivedBytes = 0, TotalBytes = 1024 }));
    }

    [Fact]
    public void TextTheCliDidNotWriteLosesWhatATerminalWouldActOn()
    {
        var shown = CliText.Printable("qBit\u001b[2J‮torrent");
        Assert.DoesNotContain('\u001b', shown);
        Assert.DoesNotContain('‮', shown);
        Assert.StartsWith("qBit", shown, StringComparison.Ordinal);
        Assert.EndsWith("torrent", shown, StringComparison.Ordinal);
        Assert.Equal(string.Empty, CliText.Printable(null));

        var cleaned = CliText.Clean("Site\u001b[2J at https://user:pw@example.invalid/x");
        Assert.DoesNotContain('\u001b', cleaned);
        Assert.DoesNotContain("pw@", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void ATorrentsNameFilesAndFolderLoseWhatATerminalWouldActOn()
    {
        var line = TorrentCommands.FileLine(new TorrentFileDto { Index = 0, Path = "a\u001b[2J‮b.mkv", Size = 10, Wanted = true });
        Assert.DoesNotContain('\u001b', line);
        Assert.DoesNotContain('‮', line);
        Assert.Contains("b.mkv", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostErrorIsToldApartFromAHostThatDoesNotKnowTheCommand()
    {
        var error = IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "The file is in use." });
        Assert.Equal("The file is in use.", CliText.HostError(error));
        var unknown = IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type 'torrent.import'." });
        Assert.Null(CliText.HostError(unknown));
        Assert.Null(CliText.HostError(IpcMessage.Create(TorrentMessageTypes.ImportResult, new TorrentImportResult { Ok = true })));
    }
}
