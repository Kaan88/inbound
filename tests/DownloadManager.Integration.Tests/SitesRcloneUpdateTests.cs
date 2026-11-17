using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// rclone download and updates through <c>sites.*</c> (remote-sites.md, "Getting rclone", "rclone updates"): a real
/// <see cref="SitesCommands"/> with a real <see cref="RcloneUpdater"/> against rclone's two hosts served from memory. The
/// signature check itself is covered in <c>RcloneReleaseSignatureTests</c>; here the list is taken as signed. Each "rclone"
/// is a shell script that prints its version, and rclone's process is a fake, so nothing real starts (Linux only).
/// </summary>
public sealed class SitesRcloneUpdateTests : IAsyncLifetime
{
    private static readonly RclonePlatform Platform = new("linux", "amd64");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbound-sites-rclone-update", Guid.NewGuid().ToString("N"));
    private readonly FakeServer _server = new();
    private readonly Dictionary<string, DateTimeOffset> _signedAt = [];
    private readonly List<SitesCommands> _commands = [];
    private readonly List<string> _log = [];
    private readonly ManualClock _time = new(Now);

    private string State => Path.Combine(_root, "state");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(State);
        PortableSecrets.UseAKeyFileOffWindows();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var commands in _commands) await commands.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [LinuxOnlyFact]
    public async Task DownloadingRcloneMakesItTheOneInUseAndIsRefusedWhenOffOrWithAnOwnRclone()
    {
        Publish("1.77.0", signedDaysAgo: 10, latest: true);
        var commands = Open();

        var off = await Call<SitesStatusDto>(commands, SitesMessageTypes.RcloneInstall, new SitesRcloneInstallRequest());
        Assert.Equal("Remote files is off. Turn it on in Options › Remote files.", off.Error);
        Assert.Equal(0, _server.Requests);

        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        Assert.Null((await Call<SitesStatusDto>(commands, SitesMessageTypes.Get)).RcloneSource);

        var installed = await Call<SitesStatusDto>(commands, SitesMessageTypes.RcloneInstall, new SitesRcloneInstallRequest());
        Assert.Null(installed.Error);
        Assert.Equal(("1.77.0", SitesRcloneSources.Downloaded, false), (installed.RcloneInstalled, installed.RcloneSource, installed.RcloneInstalling));
        Assert.True(installed.AutoUpdateRclone);

        var own = Path.Combine(_root, "my-rclone");
        File.WriteAllText(own, "#!/bin/sh\necho 'rclone v1.76.0'\n");
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = own });
        var refused = await Call<SitesStatusDto>(commands, SitesMessageTypes.RcloneInstall, new SitesRcloneInstallRequest());
        Assert.Contains("does not download or update it", refused.Error);
        Assert.Equal(SitesRcloneSources.Own, refused.RcloneSource);
    }

    [LinuxOnlyFact]
    public async Task TheDailyCheckInstallsAReadyUpdateOnlyWhenAutomaticUpdatesAreOn()
    {
        Publish("1.77.0", signedDaysAgo: 30, latest: true);
        var commands = Open();
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, AutoUpdateRclone = false });
        await Call<SitesStatusDto>(commands, SitesMessageTypes.RcloneInstall, new SitesRcloneInstallRequest());

        Publish("1.78.0", signedDaysAgo: 10, latest: true);
        await commands.UpdateTickAsync(CancellationToken.None);
        var status = commands.Status();
        Assert.Equal(("1.77.0", "1.78.0", "ready"), (status.RcloneInstalled, status.RcloneUpdate?.Version, status.RcloneUpdate?.State));
        Assert.Equal(Now, status.RcloneLastCheck);

        // Within a day the server is not asked again.
        var asked = _server.Requests;
        await commands.UpdateTickAsync(CancellationToken.None);
        Assert.Equal(asked, _server.Requests);

        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, AutoUpdateRclone = true });
        await commands.UpdateTickAsync(CancellationToken.None);
        status = commands.Status();
        Assert.Equal("1.78.0", status.RcloneInstalled);
        Assert.Null(status.RcloneUpdate);
    }

    [LinuxOnlyFact]
    public async Task NothingIsCheckedWithSitesOff()
    {
        Publish("1.77.0", signedDaysAgo: 30, latest: true);
        var commands = Open();
        await commands.UpdateTickAsync(CancellationToken.None);
        Assert.Equal(0, _server.Requests);
    }

    [LinuxOnlyFact]
    public async Task AnUpdateIsProvenByItsFirstUseAndOneThatFailsTwiceGoesBackToTheVersionBefore()
    {
        Publish("1.77.0", signedDaysAgo: 30, latest: true);
        var starts = new List<string>();
        var commands = Open((rclone, state) => new RcloneSupervisor(ct =>
        {
            lock (starts) starts.Add(rclone);
            return rclone.Contains("1.78.0", StringComparison.Ordinal)
                ? throw new IOException("rclone 1.78.0 crashed at start")
                : Task.FromResult<IRcloneInstance>(new FakeInstance());
        }, new RcloneSupervisorOptions { IdleDelay = TimeSpan.FromMilliseconds(20), FirstRestartDelay = TimeSpan.FromMilliseconds(1) }));
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        await Call<SitesStatusDto>(commands, SitesMessageTypes.RcloneInstall, new SitesRcloneInstallRequest());
        var site = await SaveTrustedSite(commands);

        // The first use starts 1.77.0 (the call itself fails: the fake rclone answers nothing) and proves it.
        await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = site });
        await WaitFor(() => commands.Status().Rclone == SitesRcloneStates.Off);

        Publish("1.78.0", signedDaysAgo: 10, latest: true);
        await commands.UpdateTickAsync(CancellationToken.None);
        Assert.Equal("1.78.0", commands.Status().RcloneInstalled);

        // 1.78.0 fails to start twice before it ever worked: refused, and 1.77.0 is in use again.
        var first = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = site });
        Assert.Contains("crashed at start", first.Message);
        Assert.Equal("1.78.0", commands.Status().RcloneInstalled);
        await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = site });
        Assert.Equal("1.77.0", commands.Status().RcloneInstalled);
        Assert.False(Directory.Exists(Path.Combine(State, "tools", "rclone", "1.78.0")));

        await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = site });
        lock (starts) Assert.EndsWith(Path.Combine("1.77.0", "rclone"), starts[^1]);

        // Never offered again.
        _time.Now = Now.AddDays(2);
        await commands.UpdateTickAsync(CancellationToken.None);
        Assert.Null(commands.Status().RcloneUpdate);
        Assert.Contains(_log, line => line.Contains("1.78.0 failed after it was installed", StringComparison.Ordinal));
    }

    [LinuxOnlyFact]
    public async Task AShippedRcloneIsReplacedByAReadyUpdateButNoFirstDownloadHappensOnItsOwn()
    {
        Publish("1.77.0", signedDaysAgo: 30, latest: true);
        var none = Open();
        await Call<SitesStatusDto>(none, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        await none.UpdateTickAsync(CancellationToken.None);
        Assert.Null(none.Status().RcloneInstalled); // nothing in use: the user's "Download rclone"
        await none.DisposeAsync();
        _commands.Remove(none);

        var shipped = Path.Combine(_root, "image", "rclone");
        Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
        File.WriteAllText(shipped, "#!/bin/sh\necho 'rclone v1.76.0'\n");
        RcloneUpdater? updater = null;
        var commands = Open(findRclone: _ => updater!.CurrentExecutable ?? shipped, created: u => updater = u);
        Assert.Equal(SitesRcloneSources.Shipped, commands.Status().RcloneSource);
        await commands.UpdateTickAsync(CancellationToken.None);
        var status = commands.Status();
        Assert.Equal(("1.77.0", SitesRcloneSources.Downloaded), (status.RcloneInstalled, status.RcloneSource));
    }

    private SitesCommands Open(Func<string, string, RcloneSupervisor>? createSupervisor = null,
        Func<string?, string?>? findRclone = null, Action<RcloneUpdater>? created = null)
    {
        var updater = new RcloneUpdater(State,
            new RcloneDownloads(new HttpClient(_server), verify: text => new RcloneSignedText(text, _signedAt[text])),
            Platform, Log, _time, (_, _, _) => Task.FromResult<string?>(null));
        created?.Invoke(updater);
        // As the Host finds rclone, minus INBOUND_RCLONE: a developer's real rclone set for the end-to-end tests must not win here.
        findRclone ??= own => RcloneSetup.Find(AppContext.BaseDirectory, own, fromEnvironment: null, updater.CurrentExecutable,
            OperatingSystem.IsWindows(), File.Exists);
        var commands = new SitesCommands(State, Log, findRclone: findRclone, createSupervisor: createSupervisor,
            probe: (_, _, _) => Task.FromResult<IReadOnlyList<SshHostKey>>([]), updater: updater, time: _time,
            firstCheckDelay: TimeSpan.FromDays(1));
        _commands.Add(commands);
        return commands;
    }

    private void Log(string line)
    {
        lock (_log) _log.Add(line);
    }

    private static async Task<string> SaveTrustedSite(SitesCommands commands)
    {
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "Box", Kind = SiteKinds.Sftp, Host = "srv.example", Port = 22, User = "u",
            AuthMethod = SiteAuthMethods.Password, Password = "pw-for-tests",
        });
        Assert.True(saved.Ok, saved.Message);
        var name = "ssh-ed25519"u8.ToArray();
        var blob = new byte[4 + name.Length + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)name.Length);
        name.CopyTo(blob, 4);
        var trusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = saved.Site!.Id, Entries = ["ssh-ed25519 " + Convert.ToBase64String(blob)] });
        Assert.True(trusted.Ok, trusted.Message);
        return saved.Site.Id;
    }

    private void Publish(string version, int signedDaysAgo, bool latest = false)
    {
        var v = Version.Parse(version);
        var zip = Zip(version);
        var name = Platform.ArchiveName(v);
        var list = $"{Convert.ToHexStringLower(SHA256.HashData(zip))}  {name}";
        _signedAt[list] = Now.AddDays(-signedDaysAgo);
        _server.Files[$"https://downloads.rclone.org/v{version}/SHA256SUMS"] = Encoding.UTF8.GetBytes(list);
        _server.Files[$"https://github.com/rclone/rclone/releases/download/v{version}/SHA256SUMS"] = Encoding.UTF8.GetBytes(list);
        _server.Files[$"https://downloads.rclone.org/v{version}/{name}"] = zip;
        if (latest) _server.Files["https://downloads.rclone.org/version.txt"] = Encoding.UTF8.GetBytes($"rclone v{version}\n");
    }

    private static byte[] Zip(string version)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry($"rclone-v{version}-linux-amd64/rclone").Open());
            writer.Write($"#!/bin/sh\necho 'rclone v{version}'\n");
        }

        return buffer.ToArray();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private static async Task<T> Call<T>(SitesCommands commands, string type, object? payload = null) =>
        (await commands.HandleAsync(IpcMessage.Create(type, payload ?? new { }), CancellationToken.None)).GetPayload<T>()!;

    private sealed class FakeServer : HttpMessageHandler
    {
        private int _requests;
        public Dictionary<string, byte[]> Files { get; } = [];
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(Files.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeInstance : IRcloneInstance
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RcloneClient Client { get; } = new(new Uri("http://127.0.0.1:9/"), "u", "pass-for-tests");
        public int ProcessId => 4242;
        public Task Exited => _exited.Task;

        public Task<bool> StopAsync(TimeSpan grace)
        {
            _exited.TrySetResult();
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Uses shell scripts as stand-in rclone binaries (Linux only).";
    }
}
