using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>
/// The probe against a real OpenSSH <c>sshd</c> run unprivileged on a free loopback port with its own host keys (no root:
/// an unprivileged sshd cannot authenticate anyone, which the probe never tries). Skipped where sshd or ssh-keygen is missing.
/// </summary>
public sealed class SshHostKeyProbeTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "inbound-ssh-probe", Guid.NewGuid().ToString("N"));
    private Process? _sshd;
    private int _port;
    private readonly Dictionary<string, string> _fingerprints = [];

    public async Task InitializeAsync()
    {
        if (SshdFactAttribute.Missing is not null) return;
        Directory.CreateDirectory(_folder);
        foreach (var (type, file) in new[] { ("ed25519", "ed25519"), ("ecdsa", "ecdsa"), ("rsa", "rsa") })
        {
            var key = Path.Combine(_folder, file);
            await Run("ssh-keygen", "-q", "-t", type, "-N", "", "-f", key);
            var line = await Run("ssh-keygen", "-l", "-E", "sha256", "-f", key + ".pub");
            _fingerprints[type] = line.Split(' ')[1];
        }

        _port = FreePort();
        var config = Path.Combine(_folder, "sshd_config");
        await File.WriteAllTextAsync(config, string.Join('\n',
            $"Port {_port}", "ListenAddress 127.0.0.1",
            $"HostKey {_folder}/ed25519", $"HostKey {_folder}/ecdsa", $"HostKey {_folder}/rsa",
            $"PidFile {_folder}/sshd.pid", "UsePAM no", "StrictModes no", "PasswordAuthentication no",
            "KbdInteractiveAuthentication no", "AuthorizedKeysFile none", "LogLevel ERROR", ""));
        _sshd = Process.Start(new ProcessStartInfo(SshdFactAttribute.Sshd, ["-D", "-e", "-f", config])
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
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, _port);
                break;
            }
            catch (SocketException) when (DateTime.UtcNow < until && !_sshd.HasExited)
            {
                await Task.Delay(50);
            }
        }
    }

    public Task DisposeAsync()
    {
        try { _sshd?.Kill(); } catch (InvalidOperationException) { }
        _sshd?.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [SshdFact]
    public async Task The_servers_pick_is_ed25519_with_the_same_fingerprint_as_ssh_keygen()
    {
        var result = await SshHostKeyProbe.ProbeAsync("127.0.0.1", _port);
        Assert.Equal("ssh-ed25519", result.Algorithm);
        Assert.Equal("ssh-ed25519", result.Key.KeyType);
        Assert.Equal(_fingerprints["ed25519"], result.Key.Fingerprint);
        Assert.StartsWith("SSH-2.0-OpenSSH", result.ServerVersion);
        Assert.StartsWith("ssh-ed25519 AAAA", result.Key.HostKeysEntry);
    }

    [SshdFact]
    public async Task All_three_keys_are_read_one_connection_each()
    {
        var keys = await SshHostKeyProbe.ProbeAllAsync("127.0.0.1", _port);
        Assert.Equal(["ssh-ed25519", "ecdsa-sha2-nistp256", "ssh-rsa"], keys.Select(k => k.KeyType));
        Assert.Equal([_fingerprints["ed25519"], _fingerprints["ecdsa"], _fingerprints["rsa"]], keys.Select(k => k.Fingerprint));
    }

    [SshdFact]
    public async Task The_HostKeysEntry_equals_the_public_key_file()
    {
        var result = await SshHostKeyProbe.ProbeAsync("127.0.0.1", _port, "rsa-sha2-512");
        var file = (await File.ReadAllTextAsync(Path.Combine(_folder, "rsa.pub"))).Split(' ');
        Assert.Equal(file[0] + " " + file[1], result.Key.HostKeysEntry);
    }

    [SshdFact]
    public async Task Asking_for_a_type_the_server_lacks_says_so()
    {
        var error = await Assert.ThrowsAsync<SshProbeException>(() => SshHostKeyProbe.ProbeAsync("127.0.0.1", _port, "ecdsa-sha2-nistp521"));
        Assert.Contains("does not offer a ecdsa-sha2-nistp521", error.Message);
    }

    [Fact]
    public async Task Nothing_listening_is_a_clear_error()
    {
        var error = await Assert.ThrowsAsync<SshProbeException>(() => SshHostKeyProbe.ProbeAsync("127.0.0.1", FreePort(), timeout: TimeSpan.FromSeconds(5)));
        Assert.Contains("Could not connect", error.Message);
    }

    [Fact]
    public async Task A_server_that_is_not_ssh_is_a_clear_error()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.WriteAsync("HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray());
            client.Client.Shutdown(SocketShutdown.Send);
            await Task.Delay(500);
        });
        var error = await Assert.ThrowsAsync<SshProbeException>(() =>
            SshHostKeyProbe.ProbeAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeout: TimeSpan.FromSeconds(5)));
        Assert.Contains("is this an SSH server", error.Message);
        await serve;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> Run(string file, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{file} failed ({process.ExitCode}).");
        return output.Trim();
    }
}

/// <summary>A fact that needs OpenSSH's sshd and ssh-keygen; skipped, saying why, without them.</summary>
public sealed class SshdFactAttribute : FactAttribute
{
    public const string Sshd = "/usr/bin/sshd";

    public static readonly string? Missing = !OperatingSystem.IsLinux() ? "Runs on Linux only (an unprivileged OpenSSH sshd)."
        : !File.Exists(Sshd) ? "No OpenSSH sshd at /usr/bin/sshd."
        : !File.Exists("/usr/bin/ssh-keygen") ? "No ssh-keygen at /usr/bin/ssh-keygen."
        : null;

    public SshdFactAttribute()
    {
        if (Missing is not null) Skip = Missing;
    }
}

public sealed class SshHostKeyProblemTests
{
    [Fact]
    public void The_changed_key_message_gives_both_fingerprints()
    {
        const string message = "rclone operations/list failed (500): NewFs: couldn't connect SSH: ssh: handshake failed: sftp: host key " +
            "mismatch for 127.0.0.1:46022: server offered ssh-ed25519 key with fingerprint SHA256:AbC+/123xyz; pinned fingerprints " +
            "for this algorithm: SHA256:OldOne/456 -- if the server key has changed legitimately, clear host_keys and re-run with --sftp-pin-host-key";
        var problem = SshHostKeyProblem.Parse(message)!;
        Assert.Equal(SshHostKeyProblemKind.Changed, problem.Kind);
        Assert.Equal("ssh-ed25519", problem.OfferedType);
        Assert.Equal("SHA256:AbC+/123xyz", problem.OfferedFingerprint);
        Assert.Equal(["SHA256:OldOne/456"], problem.PinnedFingerprints);
    }

    [Fact]
    public void Several_pinned_fingerprints_are_all_read()
    {
        const string message = "host key mismatch for example.org:22: server offered ssh-ed25519 key with fingerprint SHA256:New; " +
            "pinned fingerprints for this algorithm: SHA256:One, SHA256:Two -- if the server key has changed legitimately";
        Assert.Equal(["SHA256:One", "SHA256:Two"], SshHostKeyProblem.Parse(message)!.PinnedFingerprints);
    }

    [Fact]
    public void The_type_no_longer_offered_message_lists_what_the_server_offers()
    {
        const string message = "ssh: handshake failed: ssh: no common algorithm for host key; we offered: [\"ssh-ed25519\"], " +
            "peer offered: [\"rsa-sha2-512\" \"rsa-sha2-256\"]";
        var problem = SshHostKeyProblem.Parse(message)!;
        Assert.Equal(SshHostKeyProblemKind.TypeNoLongerOffered, problem.Kind);
        Assert.Equal(["rsa-sha2-512", "rsa-sha2-256"], problem.OfferedTypes);
        Assert.Null(problem.OfferedFingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("couldn't connect SSH: dial tcp 127.0.0.1:1: connect: connection refused")]
    [InlineData("ssh: handshake failed: ssh: unable to authenticate, attempted methods [none password]")]
    public void Other_errors_are_not_host_key_problems(string message) => Assert.Null(SshHostKeyProblem.Parse(message));
}
