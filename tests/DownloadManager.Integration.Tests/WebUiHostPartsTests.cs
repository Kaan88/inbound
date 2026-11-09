using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Host;
using DownloadManager.Runtime.WebAuth;
using DownloadManager.Runtime.WebUi;

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

    private const string Bridge = @"C:\Apps\Inbound\DownloadManager.Server.exe";

    /// <summary>The rule the window adds: inbound allow, the bridge, TCP, the port, the local subnet, Private.</summary>
    private static FirewallRuleFacts WindowsRule(string? remote = "LocalSubnet", int profiles = WindowsFirewallRules.PrivateProfile, string ports = "8080") =>
        new(WebUiDefaults.RuleName, Bridge, true, WindowsFirewallRules.Allow, WindowsFirewallRules.Tcp, ports, remote, profiles);

    private static FirewallRuleFacts BlockRule(int profiles, int protocol = WindowsFirewallRules.Tcp) =>
        new("DownloadManager.Server.exe", Bridge, true, WindowsFirewallRules.Block, protocol, "*", "*", profiles);

    private static string Evaluate(int current, params FirewallRuleFacts[] rules) =>
        WindowsFirewallRules.Evaluate(rules, Bridge, 8080, WebUiDefaults.RuleName, current);

    [Fact]
    public void TheWindowsRuleIsReadyOnlyAsTheWindowMakesItForAProfileInForce()
    {
        const int privateNet = WindowsFirewallRules.PrivateProfile;
        const int publicNet = WindowsFirewallRules.PublicProfile;
        Assert.Equal(WebUiFirewallStates.Ready, Evaluate(privateNet, WindowsRule()));
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet));
        // Widened to any address, or to another name, port, protocol or program: not the window's rule.
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet, WindowsRule(remote: "*")));
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet, WindowsRule(remote: "LocalSubnet,10.0.0.0/8")));
        Assert.Equal(WebUiFirewallStates.Ready, Evaluate(privateNet, WindowsRule(remote: "localsubnet")));
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet, WindowsRule(ports: "9090")));
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet, WindowsRule() with { Name = "Something else" }));
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet, WindowsRule() with { Protocol = 17 }));
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet, WindowsRule() with { Program = @"C:\Apps\Other.exe" }));
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet, WindowsRule() with { Enabled = false }));

        // A Private rule while the network in force is Public lets nothing in; ticking Public in the window adds it.
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(publicNet, WindowsRule()));
        Assert.Equal(WebUiFirewallStates.Ready, Evaluate(publicNet, WindowsRule(profiles: privateNet | publicNet)));
        Assert.Equal(WebUiFirewallStates.Ready, Evaluate(privateNet | publicNet, WindowsRule()));
        Assert.Equal(WebUiFirewallStates.Ready, Evaluate(privateNet, WindowsRule(profiles: 0x7FFFFFFF)));
        // No profile read: the window's rule's own one.
        Assert.Equal(WebUiFirewallStates.Ready, Evaluate(0, WindowsRule()));
    }

    [Fact]
    public void ABlockRuleBlocksOnlyTheProfilesItCovers()
    {
        const int privateNet = WindowsFirewallRules.PrivateProfile;
        const int publicNet = WindowsFirewallRules.PublicProfile;
        // Windows' own prompt answered "Cancel" on a Public network leaves a Public block; the Private rule still works.
        Assert.Equal(WebUiFirewallStates.Ready, Evaluate(privateNet, WindowsRule(), BlockRule(publicNet)));
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet, WindowsRule(), BlockRule(privateNet)));
        Assert.Equal(WebUiFirewallStates.Missing, Evaluate(privateNet, WindowsRule(), BlockRule(privateNet, WindowsFirewallRules.AnyProtocol)));
        Assert.Equal(WebUiFirewallStates.Ready, Evaluate(privateNet, WindowsRule(), BlockRule(privateNet, protocol: 17)));
        Assert.Equal(WebUiFirewallStates.Ready, Evaluate(privateNet, WindowsRule(), BlockRule(privateNet) with { Enabled = false }));
        // Two networks in force: one that lets it in is enough.
        Assert.Equal(WebUiFirewallStates.Ready,
            Evaluate(privateNet | publicNet, WindowsRule(profiles: privateNet | publicNet), BlockRule(publicNet)));
    }

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
        Assert.Equal(ListenerOwner.Ours, owner.Owner(Environment.ProcessId, port));
        Assert.Equal(ListenerOwner.Other, owner.Owner(Environment.ProcessId + 4, port));
        listener.Stop();
        Assert.Equal(ListenerOwner.None, owner.Owner(Environment.ProcessId, port));
    }

    [Fact]
    public async Task ThePasswordIsKeptAsAHashAndSettingItUnpairsEveryAppAndEndsTheSessions()
    {
        var passwords = new WebUiPasswordFile(_data, _ => { });
        Assert.False(passwords.HasPassword());
        Assert.Equal("The password needs at least 8 characters.", passwords.Problem("short"));
        Assert.Null(passwords.Problem("long enough"));

        Assert.False(await passwords.IsCurrentAsync("first password", CancellationToken.None));
        await passwords.SetAsync("first password", false, CancellationToken.None);
        Assert.True(passwords.HasPassword());
        Assert.True(await passwords.IsCurrentAsync("first password", CancellationToken.None));
        Assert.False(await passwords.IsCurrentAsync("first passwore", CancellationToken.None));
        var store = AuthStore.Open(_data);
        Assert.True(await PasswordHash.VerifyAsync("first password", store.Password));
        Assert.DoesNotContain("first password", await File.ReadAllTextAsync(Path.Combine(_data, AuthStore.FileName)));

        store.AddToken(new TokenRecord { Id = "a", Name = "ext", Hash = new string('0', 64), CreatedAt = DateTimeOffset.UtcNow }, 50);
        store.AddToken(new TokenRecord { Id = "b", Name = "desk", Hash = new string('1', 64), CreatedAt = DateTimeOffset.UtcNow, Role = TokenRoles.RemoteClient }, 50);
        await File.WriteAllTextAsync(Path.Combine(_data, WebUiPasswordFile.SessionsFileName), "{}");

        await passwords.SetAsync("second password", false, CancellationToken.None);
        var after = AuthStore.Open(_data);
        Assert.True(await PasswordHash.VerifyAsync("second password", after.Password));
        Assert.Empty(after.Tokens());
        Assert.False(File.Exists(Path.Combine(_data, WebUiPasswordFile.SessionsFileName)));
        Assert.False(File.Exists(Path.Combine(_data, AuthStore.FileName + ".tmp")));

        // Keeping the paired browsers keeps the extensions only; a desktop app always pairs again.
        after.AddToken(new TokenRecord { Id = "c", Name = "ext", Hash = new string('2', 64), CreatedAt = DateTimeOffset.UtcNow }, 50);
        after.AddToken(new TokenRecord { Id = "d", Name = "desk", Hash = new string('3', 64), CreatedAt = DateTimeOffset.UtcNow, Role = TokenRoles.RemoteClient }, 50);
        await passwords.SetAsync("third password", true, CancellationToken.None);
        Assert.Equal(["c"], AuthStore.Open(_data).Tokens().Select(token => token.Id));
    }

    [Fact]
    public async Task EndingTheSignInsRemovesTheSavedSessionsOnlyAndTheBridgeReadsWhatTheHostWrote()
    {
        var passwords = new WebUiPasswordFile(_data, _ => { });
        await passwords.SetAsync("a host password", false, CancellationToken.None);
        AuthStore.Open(_data).AddToken(new TokenRecord { Id = "a", Name = "ext", Hash = new string('0', 64), CreatedAt = DateTimeOffset.UtcNow }, 50);
        await File.WriteAllTextAsync(Path.Combine(_data, WebUiPasswordFile.SessionsFileName), "{}");
        passwords.EndSignIns();
        Assert.False(File.Exists(Path.Combine(_data, WebUiPasswordFile.SessionsFileName)));
        passwords.EndSignIns(); // nothing there: nothing to say
        var store = AuthStore.Open(_data);
        Assert.Single(store.Tokens());
        // One format, one copy: the record the Host wrote is the one the bridge and the server check.
        Assert.Equal("app", store.Password!.Source);
        Assert.Equal(PasswordHash.Algorithm, store.Password.Algorithm);
        Assert.True(await PasswordHash.VerifyAsync("a host password", store.Password));
    }

    [Fact]
    public async Task ADamagedSignInFileCountsAsNoPasswordAndIsReplacedByANewOne()
    {
        var path = Path.Combine(_data, AuthStore.FileName);
        await File.WriteAllTextAsync(path, "{ damaged");
        var passwords = new WebUiPasswordFile(_data, _ => { });
        Assert.False(passwords.HasPassword());
        await passwords.SetAsync("a new password", false, CancellationToken.None);
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
        // The same rule as the server's (one definition): a reference it makes is one the Host reads.
        var (made, reference) = UploadReferences.NewName();
        Assert.Equal(UploadReferences.FileFor(Path.Combine(_data, UploadReferences.FolderName), made), HostUploads.Resolve(_data, reference));
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

    private WebBridgeStartDto Start(int port, string? data = null) => new()
    {
        DataDirectory = data ?? _data,
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
        await new WebUiPasswordFile(_data, _ => { }).SetAsync("a test password", false, CancellationToken.None);
        var launcher = new WebBridgeLauncher(_ => { }, SandboxHost.BridgeExecutable);
        var port = FreePort();
        var process = launcher.Start(Start(port));
        await process.Ready.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ListenerOwner.Ours, new TcpListenerOwner().Owner(process.ProcessId, port));

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
        Assert.Equal(ListenerOwner.None, new TcpListenerOwner().Owner(process.ProcessId, port));
        Assert.True(Directory.Exists(Path.Combine(_data, "uploads")));
        Assert.Contains(Directory.GetFiles(Path.Combine(_data, "logs")), file => Path.GetFileName(file).StartsWith("web-bridge-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRealBridgeTakesADataFolderWhoseNameIsNotAscii()
    {
        // The data folder is under the user's profile, so it holds the Windows user's name; the options go on standard
        // input in UTF-8 both ends, whatever the hidden console's code page.
        var data = Path.Combine(_data, "Kılıç-数据-Ünal");
        Directory.CreateDirectory(data);
        await new WebUiPasswordFile(data, _ => { }).SetAsync("a test password", false, CancellationToken.None);
        var launcher = new WebBridgeLauncher(_ => { }, SandboxHost.BridgeExecutable);
        var port = FreePort();
        var process = launcher.Start(Start(port, data));
        var first = await Task.WhenAny(process.Ready, process.Exited).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(first == process.Ready, process.Exited.IsCompleted ? $"{process.Exited.Result.ExitCode}: {process.Exited.Result.Message}" : "neither");
        Assert.Contains(Directory.GetFiles(Path.Combine(data, "logs")), file => Path.GetFileName(file).StartsWith("web-bridge-", StringComparison.Ordinal));
        await process.StopAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebBridgeExit.Stopped, (await process.Exited.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
    }
}
