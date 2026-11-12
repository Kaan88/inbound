using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// <c>dm queue</c>, <c>schedule</c>, <c>site</c>, <c>profile</c> and <c>settings</c> through the real CLI and Host (audit 4:
/// M3, M4, H1, L16, L17, L27). One Host for all of them, as each starts a process.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class CliSettingsCommandTests
{
    private const string Api = "https://example.invalid/v1/api/";

    [Fact]
    public async Task QueueMaxSetsTheLimitAndRefusesAMissingOrWrongNumber()
    {
        await using var host = await SandboxHost.StartAsync(Api);
        await using var connection = await host.ConnectAsync();
        var queue = (await SettingsOf(connection)).Queues[0];

        var set = await host.RunCliAsync(null, "queue", "max", queue.Id.ToString(), "5");
        Assert.True(set.ExitCode == 0, set.All);
        Assert.Equal(5, (await SettingsOf(connection)).Queues.Single(q => q.Id == queue.Id).MaxConcurrentDownloads);

        string[][] wrongs = [["queue", "max", queue.Id.ToString()], ["queue", "max", queue.Id.ToString(), "many"], ["queue", "max", queue.Id.ToString(), "0"]];
        foreach (var wrong in wrongs)
        {
            var refused = await host.RunCliAsync(null, wrong);
            Assert.Equal(2, refused.ExitCode);
        }

        Assert.Equal(5, (await SettingsOf(connection)).Queues.Single(q => q.Id == queue.Id).MaxConcurrentDownloads);
    }

    [Fact]
    public async Task AnOptionBeforeThePositionalArgumentsIsNotTakenForOne()
    {
        await using var host = await SandboxHost.StartAsync(Api);
        await using var connection = await host.ConnectAsync();

        // The value of --dest and of --start come first; the address is still the address.
        var add = await host.RunCliAsync(null, "add", "--dest", host.Destination, "--start", "later", "https://example.invalid/file.bin");
        Assert.True(add.ExitCode == 0, add.All);
        var job = Assert.Single((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs);
        Assert.Equal("https://example.invalid/file.bin", job.Url);
        Assert.Equal(host.Destination.TrimEnd('\\'), job.DestinationDirectory.TrimEnd('\\'));

        var schedule = await host.RunCliAsync(null, "schedule", "add", "--name", "Night", "--stop", "06:00", "0", "23:00");
        Assert.True(schedule.ExitCode == 0, schedule.All);
        var added = Assert.Single((await SettingsOf(connection)).Schedules);
        Assert.Equal(("Night", "23:00", "06:00", 0), (added.Name, added.StartTime, added.StopTime, added.QueueId));
    }

    [Fact]
    public async Task AFlagWithoutItsValueOrAWrongNumberIsRefused()
    {
        await using var host = await SandboxHost.StartAsync(Api);
        await using var connection = await host.ConnectAsync();

        // --name followed by another flag would have named the file "--wait".
        Assert.Equal(2, (await host.RunCliAsync(null, "add", "https://example.invalid/a.bin", "--name", "--wait")).ExitCode);
        Assert.Equal(2, (await host.RunCliAsync(null, "add", "https://example.invalid/a.bin", "--connections", "abc")).ExitCode);
        Assert.Equal(2, (await host.RunCliAsync(null, "add", "https://example.invalid/a.bin", "--queue", "abc")).ExitCode);
        Assert.Equal(2, (await host.RunCliAsync(null, "update", "anything")).ExitCode);
        Assert.Empty((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs);
    }

    [Fact]
    public async Task ASitePasswordIsReadFromStandardInputAndNeverFromTheCommandLine()
    {
        await using var host = await SandboxHost.StartAsync(Api);
        await using var connection = await host.ConnectAsync();

        var onTheLine = await host.RunCliAsync(null, "site", "add", "--password", "s3cret", "example.com", "bob");
        Assert.Equal(2, onTheLine.ExitCode);
        Assert.Empty((await SettingsOf(connection)).SiteLogins);

        var piped = await host.RunCliAsync("s3cret", "site", "add", "example.com", "bob");
        Assert.True(piped.ExitCode == 0, piped.All);
        var login = Assert.Single((await SettingsOf(connection)).SiteLogins);
        Assert.Equal(("example.com", "bob", true), (login.Site, login.Username, login.HasPassword));
        Assert.DoesNotContain("s3cret", piped.All, StringComparison.Ordinal);

        var listed = await host.RunCliAsync(null, "site", "list");
        Assert.Contains("example.com", listed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", listed.All, StringComparison.Ordinal);

        Assert.Equal(2, (await host.RunCliAsync(null, "settings", "set", "proxypass", "s3cret")).ExitCode);
    }

    [Fact]
    public async Task ASettingsCommandChangesOnlyItsOwnFieldAndKeepsTheQueueStateTheHostHolds()
    {
        await using var host = await SandboxHost.StartAsync(Api);
        await using var connection = await host.ConnectAsync();
        var queue = (await SettingsOf(connection)).Queues[0];

        // A regression guard, not evidence for H1: the CLI already read the Host's settings right before each change
        // (jobs.query then, settings.get now), and each run is a new process, so neither version could save a stale
        // copy here. What is left of H1, a change landing between that read and the save, is the Host's to close.
        // Changed by another client before each command: the queue stopped, a schedule added.
        var stopped = await connection.AskAsync<SettingsResult>(MessageTypes.QueueAction, new QueueActionRequest { QueueId = queue.Id, Action = "stop" });
        Assert.True(stopped.Ok, stopped.Error);
        var elsewhere = await SettingsOf(connection);
        elsewhere.Schedules.Add(new ScheduleDto { Id = Guid.NewGuid(), Name = "Elsewhere", QueueId = queue.Id, StartTime = "01:00", Days = "Mon" });
        Assert.True((await connection.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = elsewhere })).Ok);

        var set = await host.RunCliAsync(null, "settings", "set", "maxconcurrent", "4");
        Assert.True(set.ExitCode == 0, set.All);
        var profile = await host.RunCliAsync(null, "profile", "add", "example.com", "2");
        Assert.True(profile.ExitCode == 0, profile.All);
        var schedule = await host.RunCliAsync(null, "schedule", "add", queue.Id.ToString(), "22:00", "--name", "Mine");
        Assert.True(schedule.ExitCode == 0, schedule.All);
        var mine = (await SettingsOf(connection)).Schedules.Single(s => s.Name == "Mine").Id.ToString("N")[..8];
        var disabled = await host.RunCliAsync(null, "schedule", "disable", mine);
        Assert.True(disabled.ExitCode == 0, disabled.All);

        var now = await SettingsOf(connection);
        Assert.Equal(4, now.MaxConcurrentDownloads);
        Assert.False(now.Queues.Single(q => q.Id == queue.Id).IsRunning);
        Assert.Contains(now.Schedules, s => s.Name == "Elsewhere");
        Assert.False(now.Schedules.Single(s => s.Name == "Mine").Enabled);
        Assert.Equal(2, Assert.Single(now.ConnectionProfiles).MaxConnections);
    }

    [Fact]
    public async Task WaitEndsOnADownloadThatWaitsToBeStartedAndAnImportTakesTheFtpEntriesAnExportWrites()
    {
        await using var host = await SandboxHost.StartAsync(Api);
        await using var connection = await host.ConnectAsync();

        // "--start later" leaves it waiting to be started, which would have been waited on for ever.
        var wait = await host.RunCliAsync(null, "add", "https://example.invalid/later.bin", "--dest", host.Destination, "--start", "later", "--wait");
        Assert.Equal(1, wait.ExitCode);
        Assert.Contains("not started", wait.Output, StringComparison.Ordinal);

        var listFile = Path.Combine(host.Root, "list.json");
        await File.WriteAllTextAsync(listFile, JobListFile.Serialize([new JobDto { Url = "ftp://user:pw@ftp.example.invalid/pub/file.iso", DestinationDirectory = host.Destination, StartMode = "later" }]));
        var imported = await host.RunCliAsync(null, "import", listFile, "--start", "later");
        Assert.True(imported.ExitCode == 0, imported.All);
        Assert.DoesNotContain("user:pw", imported.All, StringComparison.Ordinal);
        Assert.Contains((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs, job => job.Url.StartsWith("ftp://", StringComparison.Ordinal));

        // The export goes to its file whole: written beside it and moved into place, with nothing left beside it.
        var exportFile = Path.Combine(host.Root, "export.json");
        await File.WriteAllTextAsync(exportFile, "old");
        var exported = await host.RunCliAsync(null, "export", exportFile);
        Assert.True(exported.ExitCode == 0, exported.All);
        Assert.Equal(2, JobListFile.Deserialize(await File.ReadAllTextAsync(exportFile)).Count);
        Assert.Empty(Directory.GetFiles(host.Root, ".export.json.*.tmp"));
    }

    private static async Task<SettingsDto> SettingsOf(HostConnection connection)
        => (await connection.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings;
}
