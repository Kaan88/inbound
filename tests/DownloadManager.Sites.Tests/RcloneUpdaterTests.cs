using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>
/// The rclone updater against a fake release server: rclone's two hosts are served from memory, the checksum list is
/// signed with the tests' own key, and the "rclone" in each zip is a shell script that prints its version (Linux only).
/// The real 1.76.0 path is covered by <see cref="The_tested_version_installs_from_the_real_release_files"/>.
/// </summary>
public sealed class RcloneUpdaterTests : IDisposable
{
    private static readonly RclonePlatform Platform = new("linux", "amd64");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _state = Path.Combine(Path.GetTempPath(), "inbound-rclone-updater", Guid.NewGuid().ToString("N"));
    private readonly TestSigningKey _key = new();
    private readonly FakeServer _server = new();
    private readonly List<string> _log = [];
    private readonly ManualTime _time = new(Now);
    private string? _selfTestFails;

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_state, recursive: true); }
        catch (IOException) { }
    }

    private RcloneUpdater Open(RclonePlatform? platform = null) => new(_state,
        new RcloneDownloads(new HttpClient(_server), verify: _key.VerifySigned), platform ?? Platform, line => { lock (_log) _log.Add(line); }, _time,
        (_, _, _) => Task.FromResult(_selfTestFails));

    private string Folder => Path.Combine(_state, "tools", "rclone");

    [LinuxFact]
    public async Task The_newest_week_old_release_is_installed_and_becomes_current()
    {
        Publish("1.77.0", signedDaysAgo: 10, latest: true);
        var updater = Open();

        Assert.Equal(new Version(1, 77, 0), await updater.InstallAsync(null, CancellationToken.None));
        Assert.Equal(Path.Combine(Folder, "1.77.0", "rclone"), updater.CurrentExecutable);
        Assert.Equal("1.77.0", updater.State.Current);
        Assert.False(updater.State.Proven);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Folder, ".*"));
        // Only the binary is unpacked.
        Assert.Equal(["rclone"], Directory.EnumerateFileSystemEntries(Path.Combine(Folder, "1.77.0")).Select(Path.GetFileName));
    }

    [LinuxFact]
    public async Task A_release_younger_than_a_week_is_offered_later_and_not_installed()
    {
        Publish("1.77.0", signedDaysAgo: 2, latest: true);
        var updater = Open();

        var offer = await updater.CheckAsync(CancellationToken.None);
        Assert.Equal(RcloneOfferState.Waiting, offer!.State);
        Assert.Equal(Now.AddDays(5), offer.WaitUntil!.Value, TimeSpan.FromSeconds(2));
        // The first download then takes the tested version, whose hash is built in: the fake one can't match it.
        Publish("1.76.0", signedDaysAgo: 30);
        var error = await Assert.ThrowsAsync<RcloneReleaseException>(() => updater.InstallAsync(null, CancellationToken.None));
        Assert.Contains("does not match the one built into Inbound", error.Message);
        Assert.Null(updater.CurrentExecutable);

        _time.Now = Now.AddDays(6);
        Assert.Equal(RcloneOfferState.Ready, (await updater.CheckAsync(CancellationToken.None))!.State);
    }

    [LinuxFact]
    public async Task A_new_major_version_is_only_offered_and_installed_when_asked()
    {
        Publish("1.77.0", signedDaysAgo: 30);
        Publish("2.0.0", signedDaysAgo: 30, latest: true);
        var updater = Open();
        await updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None);

        var offer = await updater.CheckAsync(CancellationToken.None);
        Assert.Equal(RcloneOfferState.NeedsAsk, offer!.State);
        Assert.Contains("major", offer.Note);

        await updater.InstallAsync(new Version(2, 0, 0), CancellationToken.None);
        Assert.Equal("2.0.0", updater.State.Current);
    }

    [LinuxFact]
    public async Task A_list_that_differs_between_the_two_hosts_is_refused()
    {
        Publish("1.77.0", signedDaysAgo: 10, latest: true);
        _server.Files["https://github.com/rclone/rclone/releases/download/v1.77.0/SHA256SUMS"] =
            Encoding.UTF8.GetBytes(_key.Sign(new string('0', 64) + "  rclone-v1.77.0-linux-amd64.zip", TestSigningKey.Sha256Digest));
        var updater = Open();

        var error = await Assert.ThrowsAsync<RcloneReleaseException>(() => updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None));
        Assert.Contains("differs between", error.Message);
        Assert.Null(updater.CurrentExecutable);
    }

    [LinuxFact]
    public async Task A_zip_that_does_not_match_its_line_leaves_nothing_behind()
    {
        Publish("1.77.0", signedDaysAgo: 10, latest: true);
        _server.Files["https://downloads.rclone.org/v1.77.0/rclone-v1.77.0-linux-amd64.zip"] = Zip("1.77.0", "rclone v1.77.0 but changed");
        var updater = Open();

        var error = await Assert.ThrowsAsync<RcloneReleaseException>(() => updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None));
        Assert.Contains("does not match rclone's checksum", error.Message);
        Assert.Null(updater.CurrentExecutable);
        Assert.False(Directory.Exists(Path.Combine(Folder, "1.77.0")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Folder, ".*"));
    }

    [LinuxFact]
    public async Task Redirects_are_followed_only_to_rclones_and_githubs_hosts()
    {
        Publish("1.77.0", signedDaysAgo: 10, latest: true);
        var sums = "https://github.com/rclone/rclone/releases/download/v1.77.0/SHA256SUMS";
        _server.Files["https://objects.githubusercontent.com/sums"] = _server.Files[sums];
        _server.Redirects[sums] = "https://objects.githubusercontent.com/sums";
        await Open().InstallAsync(new Version(1, 77, 0), CancellationToken.None);

        Directory.Delete(_state, recursive: true);
        _server.Redirects[sums] = "https://example.com/sums";
        var error = await Assert.ThrowsAsync<RcloneReleaseException>(() => Open().InstallAsync(new Version(1, 77, 0), CancellationToken.None));
        Assert.Contains("example.com", error.Message);
    }

    [LinuxFact]
    public async Task A_binary_reporting_another_version_or_failing_its_self_test_is_not_installed()
    {
        Publish("1.77.0", signedDaysAgo: 10, latest: true, binaryVersion: "1.70.0");
        var error = await Assert.ThrowsAsync<RcloneReleaseException>(() => Open().InstallAsync(new Version(1, 77, 0), CancellationToken.None));
        Assert.Contains("1.70.0", error.Message);

        Publish("1.77.0", signedDaysAgo: 10, latest: true);
        _selfTestFails = "no answer";
        var updater = Open();
        error = await Assert.ThrowsAsync<RcloneReleaseException>(() => updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None));
        Assert.Contains("did not start", error.Message);
        Assert.Null(updater.CurrentExecutable);
    }

    [LinuxFact]
    public async Task An_update_keeps_the_proven_version_until_the_new_one_has_run()
    {
        Publish("1.77.0", signedDaysAgo: 30);
        Publish("1.78.0", signedDaysAgo: 10, latest: true);
        var updater = Open();
        await updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None);
        await updater.MarkProvenAsync();
        Assert.Equal(RcloneOfferState.Ready, (await updater.CheckAsync(CancellationToken.None))!.State);

        await updater.InstallAsync(null, CancellationToken.None);
        Assert.Equal(("1.78.0", "1.77.0", false), (updater.State.Current, updater.State.Previous, updater.State.Proven));
        Assert.Null(updater.Offer);
        Assert.True(Directory.Exists(Path.Combine(Folder, "1.77.0")));

        await updater.MarkProvenAsync();
        Assert.Null(updater.State.Previous);
        Assert.False(Directory.Exists(Path.Combine(Folder, "1.77.0")));
        Assert.Null(await updater.CheckAsync(CancellationToken.None));
    }

    [LinuxFact]
    public async Task A_new_version_that_fails_goes_back_to_the_one_before_and_is_never_offered_again()
    {
        Publish("1.77.0", signedDaysAgo: 30);
        Publish("1.78.0", signedDaysAgo: 10, latest: true);
        var updater = Open();
        await updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None);
        await updater.MarkProvenAsync();
        await updater.InstallAsync(null, CancellationToken.None);

        Assert.True(await updater.RollBackAsync("crashed at start"));
        Assert.Equal("1.77.0", updater.State.Current);
        Assert.Contains("1.78.0", updater.State.Refused);
        Assert.False(Directory.Exists(Path.Combine(Folder, "1.78.0")));
        Assert.Null(await updater.CheckAsync(CancellationToken.None));
        await Assert.ThrowsAsync<RcloneReleaseException>(() => updater.InstallAsync(new Version(1, 78, 0), CancellationToken.None));
        // A proven version is not rolled back.
        Assert.False(await updater.RollBackAsync("later trouble"));

        // The state survives a restart.
        var again = Open();
        Assert.Equal(("1.77.0", true), (again.State.Current, again.State.Proven));
        Assert.Contains("1.78.0", again.State.Refused);
    }

    [LinuxFact]
    public async Task Versions_at_or_below_a_shipped_one_are_removed_and_leftovers_are_cleared_at_start()
    {
        Publish("1.77.0", signedDaysAgo: 30, latest: true);
        var updater = Open();
        await updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None);
        await updater.RemoveAtOrBelowAsync(new Version(1, 77, 0));
        Assert.Null(updater.CurrentExecutable);
        Assert.False(Directory.Exists(Path.Combine(Folder, "1.77.0")));

        Directory.CreateDirectory(Path.Combine(Folder, ".1.79.0.abc"));
        File.WriteAllText(Path.Combine(Folder, ".1.79.0.abc.zip"), "cut off");
        Open();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Folder, ".*"));
    }

    [LinuxFact]
    public async Task Download_with_nothing_newer_ready_keeps_a_newer_installed_version_instead_of_going_down()
    {
        Publish("1.77.0", signedDaysAgo: 30, latest: true);
        var updater = Open();
        await updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None);
        Publish("1.78.0", signedDaysAgo: 1, latest: true); // waiting
        Publish("1.76.0", signedDaysAgo: 60);              // would fail the built-in hash if it were tried

        Assert.Equal(new Version(1, 77, 0), await updater.InstallAsync(null, CancellationToken.None));
        Assert.Equal("1.77.0", updater.State.Current);
    }

    [LinuxFact]
    public async Task Reinstalling_the_kept_version_does_not_also_keep_it_as_the_one_to_go_back_to()
    {
        Publish("1.77.0", signedDaysAgo: 30);
        Publish("1.78.0", signedDaysAgo: 10, latest: true);
        var updater = Open();
        await updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None);
        await updater.MarkProvenAsync();
        await updater.InstallAsync(new Version(1, 78, 0), CancellationToken.None);
        Assert.Equal("1.77.0", updater.State.Previous);

        await updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None);
        Assert.Equal(("1.77.0", (string?)null), (updater.State.Current, updater.State.Previous));
        await updater.MarkProvenAsync();
        Assert.NotNull(updater.CurrentExecutable);
    }

    [LinuxFact]
    public async Task A_waiting_offer_is_ready_once_its_week_is_over_without_another_check()
    {
        Publish("1.77.0", signedDaysAgo: 2, latest: true);
        var updater = Open();
        Assert.Equal(RcloneOfferState.Waiting, (await updater.CheckAsync(CancellationToken.None))!.State);
        _time.Now = Now.AddDays(5).AddMinutes(1);
        Assert.Equal(RcloneOfferState.Ready, updater.Offer!.State);
    }

    [LinuxFact]
    public async Task A_damaged_zip_is_a_release_error_and_leaves_nothing()
    {
        Publish("1.77.0", signedDaysAgo: 10, latest: true);
        var junk = "not a zip at all"u8.ToArray();
        var list = $"{Convert.ToHexStringLower(SHA256.HashData(junk))}  rclone-v1.77.0-linux-amd64.zip";
        var signed = Encoding.UTF8.GetBytes(_key.Sign(list, TestSigningKey.Sha256Digest, signedAt: Now.AddDays(-10)));
        _server.Files["https://downloads.rclone.org/v1.77.0/SHA256SUMS"] = signed;
        _server.Files["https://github.com/rclone/rclone/releases/download/v1.77.0/SHA256SUMS"] = signed;
        _server.Files["https://downloads.rclone.org/v1.77.0/rclone-v1.77.0-linux-amd64.zip"] = junk;
        var updater = Open();

        await Assert.ThrowsAsync<RcloneReleaseException>(() => updater.InstallAsync(new Version(1, 77, 0), CancellationToken.None));
        Assert.Null(updater.CurrentExecutable);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Folder, ".*"));
    }

    [LinuxFact]
    public async Task No_build_for_this_platform_is_no_offer()
    {
        Publish("1.77.0", signedDaysAgo: 30, latest: true);
        Assert.Null(await Open(new RclonePlatform("linux", "riscv64")).CheckAsync(CancellationToken.None));
    }

    /// <summary>
    /// rclone's real files for 1.76.0: its signed list (fixture, checked with the built-in key) and the zip from
    /// INBOUND_RCLONE_ZIP (a path to rclone-v1.76.0-linux-amd64.zip), served from memory, with the real self-test.
    /// </summary>
    [RealRcloneZipFact]
    public async Task The_tested_version_installs_from_the_real_release_files()
    {
        var zipPath = RealRcloneZipFactAttribute.Path!;
        var sums = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rclone-v1.76.0-SHA256SUMS"));
        _server.Files["https://downloads.rclone.org/version.txt"] = "rclone v1.76.0\n"u8.ToArray();
        _server.Files["https://downloads.rclone.org/v1.76.0/SHA256SUMS"] = sums;
        _server.Files["https://github.com/rclone/rclone/releases/download/v1.76.0/SHA256SUMS"] = sums;
        _server.Files["https://downloads.rclone.org/v1.76.0/rclone-v1.76.0-linux-amd64.zip"] = File.ReadAllBytes(zipPath);
        var updater = new RcloneUpdater(_state, new RcloneDownloads(new HttpClient(_server)), Platform, line => _log.Add(line));

        Assert.Equal(new Version(1, 76, 0), await updater.InstallAsync(null, CancellationToken.None));
        var (version, _) = await RcloneSetup.ReadVersionAsync(updater.CurrentExecutable!);
        Assert.Equal(new Version(1, 76, 0), version);
    }

    private void Publish(string version, int signedDaysAgo, bool latest = false, string? binaryVersion = null)
    {
        var v = Version.Parse(version);
        var zip = Zip(version, $"rclone v{binaryVersion ?? version}");
        var name = Platform.ArchiveName(v);
        var list = $"{Convert.ToHexStringLower(SHA256.HashData(zip))}  {name}\n{new string('a', 64)}  rclone-v{version}-windows-amd64.zip";
        var signed = Encoding.UTF8.GetBytes(_key.Sign(list, TestSigningKey.Sha256Digest, signedAt: Now.AddDays(-signedDaysAgo)));
        _server.Files[$"https://downloads.rclone.org/v{version}/SHA256SUMS"] = signed;
        _server.Files[$"https://github.com/rclone/rclone/releases/download/v{version}/SHA256SUMS"] = signed;
        _server.Files[$"https://downloads.rclone.org/v{version}/{name}"] = zip;
        if (latest) _server.Files["https://downloads.rclone.org/version.txt"] = Encoding.UTF8.GetBytes($"rclone v{version}\n");
    }

    private static byte[] Zip(string version, string printed)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in new[]
                     {
                         ($"rclone-v{version}-linux-amd64/rclone", $"#!/bin/sh\necho '{printed}'\n"),
                         ($"rclone-v{version}-linux-amd64/README.txt", "readme"),
                     })
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public Dictionary<string, string> Redirects { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (Redirects.TryGetValue(url, out var to))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } });
            return Task.FromResult(Files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class RealRcloneZipFactAttribute : FactAttribute
{
    public static readonly string? Path = Environment.GetEnvironmentVariable("INBOUND_RCLONE_ZIP");

    public RealRcloneZipFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Path) || !File.Exists(Path))
            Skip = "Set INBOUND_RCLONE_ZIP to rclone-v1.76.0-linux-amd64.zip (not in the repository) to run this on Linux.";
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Uses a shell script as a stand-in rclone (Linux only).";
    }
}
