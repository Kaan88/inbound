using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Host;
using DownloadManager.Server.Auth;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// D8: the desktop Host's own web UI parts against the real thing: the firewall's rules and the TCP table are read (never
/// changed), the password file is written in a test folder, and the real bridge program is started there.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WebUiHostPartsTests : IDisposable
{
    private readonly string _data = Path.Combine(AppContext.BaseDirectory, "test-work", "webui-parts-" + Guid.NewGuid().ToString("N"));

    public WebUiHostPartsTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    [Fact]
    public void TheFirewallRulesAreReadAndAProgramWithoutARuleIsMissing()
    {
        // Reading only: a program that doesn't exist has no rule, whatever this PC's rules are. A process below medium
        // integrity (as this repository's builds run) is refused the read, says why, and counts as unknown.
        var program = Path.Combine(_data, "no-such-bridge", "DownloadManager.Server.exe");
        var warnings = new List<string>();
        var state = new WindowsFirewallRules(warnings.Add).Check(program, 18080, WebUiDefaults.RuleName);
        if (ProcessIntegrity.IsBelowMedium())
        {
            Assert.Equal(WebUiFirewallStates.Unknown, state);
            Assert.Contains(warnings, warning => warning.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(WebUiFirewallStates.Missing, state);
            Assert.Empty(warnings);
        }
    }

    [Theory]
    [InlineData("*", 8080, true)]
    [InlineData("8080", 8080, true)]
    [InlineData("80,8000-8100", 8080, true)]
    [InlineData("8000-8079", 8080, false)]
    [InlineData("", 8080, false)]
    [InlineData("RPC", 8080, false)]
    public void ARulesPortsAreReadAsWindowsWritesThem(string ports, int port, bool covered) =>
        Assert.Equal(covered, WindowsFirewallRules.CoversPort(ports, port));

    [Fact]
    public void ARulesProgramIsComparedAsAFullPathWithoutCase()
    {
        Assert.True(WindowsFirewallRules.SameProgram(@"C:\Apps\Inbound\DownloadManager.Server.exe", @"c:\apps\inbound\downloadmanager.server.exe"));
        Assert.True(WindowsFirewallRules.SameProgram(@"%SystemDrive%\Apps\x.exe", Environment.ExpandEnvironmentVariables(@"%SystemDrive%\Apps\x.exe")));
        Assert.False(WindowsFirewallRules.SameProgram(@"C:\Apps\Other.exe", @"C:\Apps\DownloadManager.Server.exe"));
        Assert.False(WindowsFirewallRules.SameProgram(null, @"C:\Apps\DownloadManager.Server.exe"));
    }

    [Fact]
    public void TheListenerTableNamesTheProcessThatListens()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var owner = new TcpListenerOwner();
        Assert.True(owner.IsListening(Environment.ProcessId, port));
        Assert.False(owner.IsListening(Environment.ProcessId + 4, port));
        listener.Stop();
        Assert.False(owner.IsListening(Environment.ProcessId, port));
    }

    [Fact]
    public async Task ThePasswordIsKeptAsAHashAndSettingItUnpairsEveryAppAndEndsTheSessions()
    {
        var passwords = new WebUiPasswordFile(_data, _ => { });
        Assert.False(passwords.HasPassword());
        Assert.Equal("The password needs at least 8 characters.", passwords.Problem("short"));
        Assert.Null(passwords.Problem("long enough"));

        await passwords.SetAsync("first password", CancellationToken.None);
        Assert.True(passwords.HasPassword());
        var store = AuthStore.Open(_data);
        Assert.True(await PasswordHash.VerifyAsync("first password", store.Password));
        Assert.DoesNotContain("first password", await File.ReadAllTextAsync(Path.Combine(_data, AuthStore.FileName)));

        store.AddToken(new TokenRecord { Id = "a", Name = "ext", Hash = new string('0', 64), CreatedAt = DateTimeOffset.UtcNow }, 50);
        store.AddToken(new TokenRecord { Id = "b", Name = "desk", Hash = new string('1', 64), CreatedAt = DateTimeOffset.UtcNow, Role = TokenRoles.RemoteClient }, 50);
        await File.WriteAllTextAsync(Path.Combine(_data, WebUiPasswordFile.SessionsFileName), "{}");

        await passwords.SetAsync("second password", CancellationToken.None);
        var after = AuthStore.Open(_data);
        Assert.True(await PasswordHash.VerifyAsync("second password", after.Password));
        Assert.Empty(after.Tokens());
        Assert.False(File.Exists(Path.Combine(_data, WebUiPasswordFile.SessionsFileName)));
        Assert.False(File.Exists(Path.Combine(_data, AuthStore.FileName + ".tmp")));
    }

    [Fact]
    public async Task ADamagedSignInFileCountsAsNoPasswordAndIsReplacedByANewOne()
    {
        var path = Path.Combine(_data, AuthStore.FileName);
        await File.WriteAllTextAsync(path, "{ damaged");
        var passwords = new WebUiPasswordFile(_data, _ => { });
        Assert.False(passwords.HasPassword());
        await passwords.SetAsync("a new password", CancellationToken.None);
        Assert.True(passwords.HasPassword());
        Assert.True(File.Exists(path + ".damaged"));
    }

    [Fact]
    public void AnUploadReferenceIsTheUploadFolderAndNothingElse()
    {
        var name = new string('a', 32);
        Assert.Equal(Path.Combine(_data, "uploads", name + ".torrent"), HostUploads.Resolve(_data, "upload:" + name));
        Assert.Null(HostUploads.Resolve(_data, "upload:../../x"));
        Assert.Null(HostUploads.Resolve(_data, "upload:" + new string('A', 32)));
        Assert.Null(HostUploads.Resolve(_data, @"C:\x.torrent"));
    }

    [Fact]
    public void TheNetworkAddressesHaveNoLoopbackOrLinkLocalOnes()
    {
        foreach (var address in new LocalNetworkAddresses().Current())
        {
            Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily);
            Assert.False(IPAddress.IsLoopback(address));
            Assert.False(address.GetAddressBytes() is [169, 254, ..]);
        }
    }

    private WebBridgeStartDto Start(int port) => new()
    {
        DataDirectory = _data,
        HelloPipe = "test-no-host-" + Guid.NewGuid().ToString("N"),
        Port = port,
        LocalOnly = true,
        TicketKey = Convert.ToBase64String(WebUiTicket.NewKey()),
    };

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task TheRealBridgeRefusesToStartWithoutAPasswordAndSaysWhy()
    {
        var launcher = new WebBridgeLauncher(_ => { }, SandboxHost.BridgeExecutable);
        var process = launcher.Start(Start(FreePort()));
        var exit = await process.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WebBridgeExit.Refused, exit.ExitCode);
        Assert.Equal("No password is set for the web UI. Set one in the app on this PC (Options, Web UI).", exit.Message);
        Assert.False(process.Ready.IsCompleted);
    }

    [Fact]
    public async Task TheRealBridgeSaysWhenItListensStopsWhenItsInputClosesAndReportsAPortInUse()
    {
        await new WebUiPasswordFile(_data, _ => { }).SetAsync("a test password", CancellationToken.None);
        var launcher = new WebBridgeLauncher(_ => { }, SandboxHost.BridgeExecutable);
        var port = FreePort();
        var process = launcher.Start(Start(port));
        await process.Ready.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(new TcpListenerOwner().IsListening(process.ProcessId, port));

        using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") })
        {
            using var status = JsonDocument.Parse(await http.GetStringAsync("/api/auth/status"));
            Assert.Equal("windows", status.RootElement.GetProperty("hostKind").GetString());
            Assert.False(status.RootElement.GetProperty("setupNeeded").GetBoolean());
        }

        // A second bridge on the same port can't take it.
        var second = launcher.Start(Start(port));
        var refused = await second.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WebBridgeExit.PortInUse, refused.ExitCode);
        Assert.Equal($"Port {port} is in use by another program. Choose another port.", refused.Message);

        await process.StopAsync(TimeSpan.FromSeconds(10));
        var exit = await process.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebBridgeExit.Stopped, exit.ExitCode);
        Assert.False(new TcpListenerOwner().IsListening(process.ProcessId, port));
        Assert.True(Directory.Exists(Path.Combine(_data, "uploads")));
        Assert.Contains(Directory.GetFiles(Path.Combine(_data, "logs")), file => Path.GetFileName(file).StartsWith("web-bridge-", StringComparison.Ordinal));
    }
}
