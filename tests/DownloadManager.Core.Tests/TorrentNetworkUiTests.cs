using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T2 network: the Windows Firewall rule's commands and reading, the port hint, and the port rules.</summary>
public sealed class TorrentNetworkUiTests
{
    private const string Install = @"C:\Users\someone\AppData\Local\Programs\Inbound Download Manager\app";
    private const string System32 = @"C:\Windows\System32";
    private static readonly string Worker = Path.Combine(Install, "inbound-torrent.exe");

    private static string Decode(FirewallCommand command)
    {
        const string marker = "-EncodedCommand ";
        var at = command.Arguments.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0);
        return Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[(at + marker.Length)..]));
    }

    [Fact]
    public void TheAllowCommandRunsSystemPowerShellAndAddsTcpAndUdpRulesForTheEngineOnly()
    {
        var command = TorrentFirewall.BuildAllowCommand(Worker, System32, Install);
        Assert.Equal(Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe"), command.FileName);
        Assert.True(WebUiFirewall.IsSystemProgram(command.FileName, System32));
        Assert.StartsWith("-NoProfile -NonInteractive -EncodedCommand ", command.Arguments);
        var script = Decode(command);
        Assert.Contains($"$program = '{Worker}'", script);
        Assert.Contains("$name = 'Inbound Download Manager torrents'", script);
        Assert.Contains(@"Import-Module 'C:\Windows\System32\WindowsPowerShell\v1.0\Modules\NetSecurity\NetSecurity.psd1'", script);
        Assert.Contains("-Protocol TCP -Profile Private,Public", script);
        Assert.Contains("-Protocol UDP -Profile Private,Public", script);
        Assert.Contains("-Direction Inbound -Action Allow -Program $program", script);
        // Windows' own block rules for the engine and this app's earlier rules go first; nothing else is touched.
        Assert.Contains("$_.Action -eq 'Block' -or $_.DisplayName -ceq $name", script);
        Assert.DoesNotContain("LocalPort", script);
        Assert.EndsWith("exit 1\n}", script);
    }

    [Fact]
    public void TheRemoveCommandIsNetshDeletingThisNameForThisProgram()
    {
        var command = TorrentFirewall.BuildRemoveCommand(Worker, System32, Install);
        Assert.Equal(Path.Combine(System32, "netsh.exe"), command.FileName);
        Assert.True(WebUiFirewall.IsSystemProgram(command.FileName, System32));
        Assert.Equal($"advfirewall firewall delete rule name=\"Inbound Download Manager torrents\" dir=in program=\"{Worker}\"", command.Arguments);
    }

    [Theory]
    [InlineData(@"C:\elsewhere\inbound-torrent.exe")]
    [InlineData(Install + @"\DownloadManager.Host.exe")]
    [InlineData(@"inbound-torrent.exe")]
    [InlineData(@"\\server\share\inbound-torrent.exe")]
    [InlineData(Install + "\\a\"b\\inbound-torrent.exe")]
    [InlineData("")]
    public void OnlyTheEngineInThisAppsFolderCanBeNamed(string path)
    {
        Assert.NotNull(TorrentFirewall.WorkerPathProblem(path, Install));
        Assert.Throws<ArgumentException>(() => TorrentFirewall.BuildAllowCommand(path, System32, Install));
        Assert.Throws<ArgumentException>(() => TorrentFirewall.BuildRemoveCommand(path, System32, Install));
    }

    [Fact]
    public void AQuoteInTheFolderStaysData()
    {
        const string folder = @"C:\Users\o'neil\Inbound";
        var script = TorrentFirewall.AllowScript(Path.Combine(folder, "inbound-torrent.exe"), System32);
        Assert.Contains(@"$program = 'C:\Users\o''neil\Inbound\inbound-torrent.exe'", script);
    }

    private static TorrentFirewallRule Rule(int action, int protocol, int profiles, string name = TorrentFirewall.RuleName, bool enabled = true, string? program = null)
        => new(name, program ?? Worker, enabled, action, protocol, profiles);

    [Fact]
    public void BothProtocolsOnANetworkInUseAreNeededToCountAsAllowed()
    {
        const int privateAndPublic = TorrentFirewall.PrivateProfile | TorrentFirewall.PublicProfile;
        var tcp = Rule(TorrentFirewall.Allow, TorrentFirewall.Tcp, privateAndPublic);
        var udp = Rule(TorrentFirewall.Allow, TorrentFirewall.Udp, privateAndPublic);

        Assert.Equal(new TorrentFirewallStatus(TorrentFirewallState.Allowed, true),
            TorrentFirewall.Evaluate([tcp, udp], Worker, TorrentFirewall.PrivateProfile));
        Assert.Equal(TorrentFirewallState.Missing, TorrentFirewall.Evaluate([tcp], Worker, TorrentFirewall.PrivateProfile).State);
        Assert.Equal(TorrentFirewallState.Missing, TorrentFirewall.Evaluate([tcp, udp], Worker, TorrentFirewall.DomainProfile).State);
        Assert.Equal(TorrentFirewallState.Missing,
            TorrentFirewall.Evaluate([tcp with { Enabled = false }, udp], Worker, TorrentFirewall.PrivateProfile).State);
        // Windows' own prompt makes rules of its own name for any protocol: they count too, but aren't this app's.
        Assert.Equal(new TorrentFirewallStatus(TorrentFirewallState.Allowed, false),
            TorrentFirewall.Evaluate([Rule(TorrentFirewall.Allow, TorrentFirewall.AnyProtocol, privateAndPublic, name: "inbound-torrent.exe")], Worker, TorrentFirewall.PublicProfile));
        // Another program's rules don't count.
        Assert.Equal(new TorrentFirewallStatus(TorrentFirewallState.Missing, false),
            TorrentFirewall.Evaluate([tcp with { Program = @"C:\other\inbound-torrent.exe" }, udp with { Program = @"C:\other\inbound-torrent.exe" }], Worker, TorrentFirewall.PrivateProfile));
        // A network not identified yet is treated as Public.
        Assert.Equal(TorrentFirewallState.Allowed, TorrentFirewall.Evaluate([tcp, udp], Worker, 0).State);
    }

    [Fact]
    public void ABlockRuleWins()
    {
        var rules = new[]
        {
            Rule(TorrentFirewall.Allow, TorrentFirewall.Tcp, TorrentFirewall.PrivateProfile),
            Rule(TorrentFirewall.Allow, TorrentFirewall.Udp, TorrentFirewall.PrivateProfile),
            Rule(TorrentFirewall.Block, TorrentFirewall.Udp, TorrentFirewall.PrivateProfile, name: "inbound-torrent.exe"),
        };
        Assert.Equal(new TorrentFirewallStatus(TorrentFirewallState.Blocked, true),
            TorrentFirewall.Evaluate(rules, Worker, TorrentFirewall.PrivateProfile));
        Assert.Contains("blocks", TorrentFirewall.Describe(TorrentFirewallState.Blocked));
    }

    [Fact]
    public void ThePortHintSaysWhatTheEngineReported()
    {
        Assert.Null(TorrentPortHint.Describe(null));
        Assert.Null(TorrentPortHint.Describe(new TorrentNetworkResult { Error = "no" }));
        Assert.Contains("hasn't run", TorrentPortHint.Describe(new TorrentNetworkResult { Ok = true }));
        Assert.Equal("The port couldn't be opened: address in use. Choose another port.",
            TorrentPortHint.Describe(new TorrentNetworkResult { Ok = true, Ran = true, ListenError = "address in use" }));
        Assert.Equal("Listening on port 51413. No peer has connected in yet.",
            TorrentPortHint.Describe(new TorrentNetworkResult { Ok = true, Ran = true, Running = true, Port = 51413 }));
        Assert.Equal("Last run listened on port 51413. Your router forwards port 51413 to it. A peer connected in, so others can reach you.",
            TorrentPortHint.Describe(new TorrentNetworkResult { Ok = true, Ran = true, Port = 51413, MappedPort = 51413, IncomingSeen = true }));
        Assert.Equal("Last run listened on port 51413. No peer connected in: a firewall or your router may be blocking the port.",
            TorrentPortHint.Describe(new TorrentNetworkResult { Ok = true, Ran = true, Port = 51413 }));
        Assert.Equal("Port 50000 couldn't be opened, so the torrent engine used port 61234 for this run. Choose another port. No peer has connected in yet.",
            TorrentPortHint.Describe(new TorrentNetworkResult { Ok = true, Ran = true, Running = true, Port = 61234, UnusablePort = 50000 }));
    }

    [Fact]
    public void APickedPortIsBelowWindowsDynamicRangeAndSkipsUnusableOnes()
    {
        var random = new Random(7);
        for (var i = 0; i < 1000; i++)
        {
            var port = TorrentListenPorts.Pick(random: random);
            Assert.InRange(port, TorrentListenPorts.PickMin, TorrentListenPorts.PickMax);
            Assert.True(port < 49152);
            Assert.True(TorrentListenPorts.IsValid(port));
        }

        // A port the check turns away (reserved by Windows, or in use) is not chosen while another one passes.
        var tried = new List<int>();
        var chosen = TorrentListenPorts.Pick(p => { tried.Add(p); return tried.Count == 3; }, new Random(1));
        Assert.Equal(3, tried.Count);
        Assert.Equal(tried[^1], chosen);
        // None passes: a few tries, then the last one (the engine listens on any free port for that run).
        var count = 0;
        TorrentListenPorts.Pick(_ => { count++; return false; }, new Random(2));
        Assert.Equal(20, count);

        Assert.False(TorrentListenPorts.IsValid(0));
        Assert.False(TorrentListenPorts.IsValid(1023));
        Assert.False(TorrentListenPorts.IsValid(65536));
        Assert.True(TorrentListenPorts.IsValid(1024));
    }

    [Fact]
    public void ATypedPortOutOfRangeIsRefusedWithAMessage()
    {
        Assert.Null(TorrentPortInput.Problem(double.NaN));
        Assert.Null(TorrentPortInput.Problem(1024));
        Assert.Null(TorrentPortInput.Problem(65535));
        Assert.Null(TorrentPortInput.Problem(30000));
        foreach (var bad in new[] { 80d, 0d, -5d, 70000d, 1e12, double.PositiveInfinity })
            Assert.Equal("The torrent port must be 1024 to 65535.", TorrentPortInput.Problem(bad));
    }

    [Fact]
    public void TheUninstallerRemovesTheSameRuleForTheInstalledEngine()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DownloadManager.iss"));
        Assert.Contains($"#define TorrentRuleName \"{TorrentFirewall.RuleName}\"", iss);
        Assert.Contains(@"WorkerPath := ExpandConstant('{app}\app\" + TorrentFirewall.WorkerFileName + "');", iss);
        Assert.Contains("Policy.Rules.Item('{#TorrentRuleName}')", iss);
        // Found without elevation first; one prompt only when the rule is there; nothing asked in a silent uninstall.
        Assert.Contains("if UninstallSilent or not TorrentFirewallRuleExists(WorkerPath) then", iss);
        Assert.Contains("ShellExec('runas', ExpandConstant('{sys}\\netsh.exe'),", iss);
        Assert.Contains("'advfirewall firewall delete rule name=\"{#TorrentRuleName}\" dir=in program=\"' + WorkerPath + '\"'", iss);
        var uninstallStep = iss[iss.IndexOf("procedure CurUninstallStepChanged", StringComparison.Ordinal)..];
        Assert.Contains("RemoveTorrentFirewallRule;", uninstallStep[..uninstallStep.IndexOf("usPostUninstall", StringComparison.Ordinal)]);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DownloadManager.slnx"))) return dir.FullName;
        throw new InvalidOperationException("The repository was not found.");
    }
}
