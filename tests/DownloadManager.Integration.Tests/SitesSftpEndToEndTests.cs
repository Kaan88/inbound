using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.2 end to end: <c>sites.*</c> on a real <see cref="SitesCommands"/> with a real rclone (INBOUND_RCLONE, 1.75.0 or
/// later) against a real OpenSSH sshd run unprivileged on a loopback port. An unprivileged sshd can log in only the user
/// running it, with a key from its own authorized_keys file, which is what this test uses. Probe, trust, test, then the
/// server's key changes and the test is refused with both fingerprints. Linux only; skipped without rclone or sshd.
/// </summary>
public sealed class SitesSftpEndToEndTests : IAsyncLifetime
{
    private const string Sshd = "/usr/bin/sshd";
    private const string SftpServer = "/usr/lib/ssh/sftp-server";
    private const string Passphrase = "Sites-e2e-passphrase-4417";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbound-sites-e2e", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly List<SitesCommands> _commands = [];
    private Process? _sshd;
    private int _port;

    private string State => Path.Combine(_root, "state");
    private string Ssh => Path.Combine(_root, "ssh");
    private string Served => Path.Combine(_root, "served");

    public static string? Missing =>
        !OperatingSystem.IsLinux() ? "Runs on Linux only (an unprivileged OpenSSH sshd)."
        : Rclone is null ? "No rclone: set INBOUND_RCLONE to an rclone 1.75.0 or later (it is not in the repository)."
        : !File.Exists(Sshd) || !File.Exists(SftpServer) || !File.Exists("/usr/bin/ssh-keygen") ? "No OpenSSH sshd, sftp-server or ssh-keygen."
        : null;

    private static string? Rclone =>
        Environment.GetEnvironmentVariable(RcloneSetup.RcloneVariable) is { Length: > 0 } path && File.Exists(path) ? path : null;

    public async Task InitializeAsync()
    {
        if (Missing is not null) return;
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Ssh);
        Directory.CreateDirectory(Path.Combine(Served, "pub"));
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "b.txt"), "b");
        PortableSecrets.UseAKeyFileOffWindows();

        await Run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(Ssh, "host"));
        await Run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(Ssh, "other-host"));
        await Run("ssh-keygen", "-q", "-t", "ed25519", "-N", Passphrase, "-f", Path.Combine(Ssh, "user"));
        File.Copy(Path.Combine(Ssh, "user.pub"), Path.Combine(Ssh, "authorized_keys"));
        _port = FreePort();
        await StartSshdAsync("host");
    }

    public async Task DisposeAsync()
    {
        foreach (var commands in _commands) await commands.DisposeAsync();
        StopSshd();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    [SitesE2EFact]
    public async Task ProbeTrustAndTestConnectWithAKeyThenAChangedServerKeyIsRefused()
    {
        var commands = new SitesCommands(State, line => { lock (_log) _log.Add(line); }, findRclone: _ => Rclone);
        _commands.Add(commands);
        // The own path is how the test points Sites at INBOUND_RCLONE; findRclone resolves it.
        var status = await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled,
            new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        Assert.True(status.Enabled);

        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "Loopback",
            Kind = SiteKinds.Sftp,
            Host = "127.0.0.1",
            Port = _port,
            User = Environment.UserName,
            AuthMethod = SiteAuthMethods.Key,
            PrivateKey = await File.ReadAllTextAsync(Path.Combine(Ssh, "user")),
            KeyPassphrase = Passphrase,
            RemoteFolder = Path.Combine(Served, "pub"),
        });
        Assert.True(saved.Ok, saved.Message);
        var id = saved.Site!.Id;

        // Before a key is accepted rclone is never asked.
        var untrusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id });
        Assert.Equal(SiteProblems.HostKeyNotAccepted, untrusted.Problem);

        var probe = await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = "127.0.0.1", Port = _port });
        Assert.True(probe.Ok, probe.Message);
        var key = Assert.Single(probe.Keys);
        Assert.Equal(await Fingerprint("host.pub"), key.Fingerprint);

        var trusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = id, Entries = [key.Entry] });
        Assert.True(trusted.Ok, trusted.Message);

        var test = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id });
        Assert.True(test.Ok, test.Message);
        Assert.Equal("Connected. The folder holds 2 items.", test.Message);

        // A wrong passphrase is rclone's "decryption password incorrect", said in plain words; the right one works again.
        var request = new SiteSaveRequest
        {
            Id = id,
            Name = "Loopback",
            Kind = SiteKinds.Sftp,
            Host = "127.0.0.1",
            Port = _port,
            User = Environment.UserName,
            AuthMethod = SiteAuthMethods.Key,
            KeyPassphrase = "not-the-passphrase",
            RemoteFolder = Path.Combine(Served, "pub"),
        };
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Save, request)).Ok);
        var wrong = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id });
        Assert.False(wrong.Ok);
        Assert.Equal("The key passphrase is wrong. Enter it again in Edit remote.", wrong.Message);
        request.KeyPassphrase = Passphrase;
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Save, request)).Ok);
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id })).Ok);

        // SITES.3: the start folder in pages of one, from one listing; the parent shows pub as a folder; a star persists.
        var first = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = id, Limit = 1 });
        Assert.True(first.Ok, first.Message);
        Assert.Equal(Path.Combine(Served, "pub"), first.Path);
        Assert.Equal(2, first.Total);
        var a = Assert.Single(first.Items);
        Assert.Equal(("a.txt", false, 1L), (a.Name, a.IsFolder, a.Size));
        Assert.NotNull(a.Modified);
        Assert.NotNull(first.Listing);
        var second = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = id, Offset = 1, Limit = 1, Listing = first.Listing });
        Assert.True(second.Ok, second.Message);
        Assert.Equal("b.txt", Assert.Single(second.Items).Name);
        Assert.Equal(first.Listing, second.Listing);
        // A later page of another listing, or of none, is never served from a fresh one.
        foreach (var listing in new[] { null, "0123" })
        {
            var stale = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = id, Offset = 1, Limit = 1, Listing = listing });
            Assert.Equal(SiteProblems.ListingChanged, stale.Problem);
            Assert.Empty(stale.Items);
        }

        var parent = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = id, Path = Served + "/" });
        Assert.True(parent.Ok, parent.Message);
        var pub = Assert.Single(parent.Items);
        Assert.Equal(("pub", true, -1L), (pub.Name, pub.IsFolder, pub.Size));

        var missing = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = id, Path = Served + "/nothing-here" });
        Assert.False(missing.Ok);
        Assert.Null(missing.Problem);

        var starred = await Call<SiteResultDto>(commands, SitesMessageTypes.Star, new SiteStarRequest { Id = id, Path = first.Path });
        Assert.Equal([first.Path], starred.Site!.Stars);

        // The same address now answers with another key: refused, with what it offers and what was accepted.
        StopSshd();
        await StartSshdAsync("other-host");
        var changed = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = id });
        Assert.False(changed.Ok);
        Assert.Equal(SiteProblems.HostKeyChanged, changed.Problem);
        Assert.Equal(await Fingerprint("other-host.pub"), changed.OfferedFingerprint);
        Assert.Equal([key.Fingerprint], changed.AcceptedFingerprints);
        var changedBrowse = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = id, Refresh = true });
        Assert.False(changedBrowse.Ok);
        Assert.Equal(SiteProblems.HostKeyChanged, changedBrowse.Problem);
        Assert.Empty(changedBrowse.Items);

        // Neither the key nor its passphrase reached the log, the replies or any file under the state folder in the clear.
        var keyLines = (await File.ReadAllLinesAsync(Path.Combine(Ssh, "user"))).Where(line => !line.StartsWith("-----", StringComparison.Ordinal) && line.Length > 0).ToArray();
        Assert.NotEmpty(keyLines);
        string log;
        lock (_log) log = string.Join('\n', _log);
        var texts = new List<string> { log, changed.Message ?? "", test.Message ?? "" };
        foreach (var file in Directory.EnumerateFiles(State, "*", SearchOption.AllDirectories)) texts.Add(await File.ReadAllTextAsync(file));
        foreach (var text in texts)
        {
            Assert.DoesNotContain(Passphrase, text);
            foreach (var line in keyLines) Assert.DoesNotContain(line, text);
        }
    }

    private async Task StartSshdAsync(string hostKey)
    {
        var config = Path.Combine(Ssh, "sshd_config");
        await File.WriteAllTextAsync(config, string.Join('\n',
            $"Port {_port}", "ListenAddress 127.0.0.1", $"HostKey {Path.Combine(Ssh, hostKey)}",
            $"PidFile {Path.Combine(Ssh, "sshd.pid")}", "UsePAM no", "StrictModes no", "PasswordAuthentication no",
            "KbdInteractiveAuthentication no", "PubkeyAuthentication yes", $"AuthorizedKeysFile {Path.Combine(Ssh, "authorized_keys")}",
            $"Subsystem sftp {SftpServer}", "LogLevel ERROR", ""));
        _sshd = Process.Start(new ProcessStartInfo(Sshd, ["-D", "-e", "-f", config])
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        _ = _sshd.StandardError.ReadToEndAsync();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, _port);
                // Something else holding the port makes our sshd exit at once; a connect alone would not show it.
                await Task.Delay(100);
                if (_sshd.HasExited) throw new InvalidOperationException($"sshd exited with {_sshd.ExitCode} (is port {_port} taken?)");
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < until && !_sshd.HasExited)
            {
                await Task.Delay(50);
            }
            catch (SocketException e)
            {
                throw new InvalidOperationException(_sshd.HasExited
                    ? $"sshd exited with {_sshd.ExitCode} (is port {_port} taken?)"
                    : $"sshd did not listen on port {_port} within 10 s", e);
            }
        }
    }

    private void StopSshd()
    {
        if (_sshd is null) return;
        try
        {
            _sshd.Kill();
            _sshd.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }

        _sshd.Dispose();
        _sshd = null;
    }

    private async Task<string> Fingerprint(string publicKeyFile) =>
        (await Run("ssh-keygen", "-l", "-E", "sha256", "-f", Path.Combine(Ssh, publicKeyFile))).Split(' ')[1];

    private static async Task<string> Run(string file, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {await process.StandardError.ReadToEndAsync()}");
        return output;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<T> Call<T>(SitesCommands commands, string type, object payload)
    {
        // A stalled rclone or sshd fails the test instead of hanging the run.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return (await commands.HandleAsync(IpcMessage.Create(type, payload), timeout.Token)).GetPayload<T>();
    }
}

public sealed class SitesE2EFactAttribute : FactAttribute
{
    public SitesE2EFactAttribute()
    {
        if (SitesSftpEndToEndTests.Missing is not null) Skip = SitesSftpEndToEndTests.Missing;
    }
}
