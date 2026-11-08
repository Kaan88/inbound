using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>The Web UI page of Options (D8): the firewall command, the page's checks and its calls to the Host.</summary>
public sealed class WebUiOptionsTests
{
    private const string System32 = @"C:\Windows\System32";
    private const string Bridge = @"C:\Program Files\Inbound Download Manager\DownloadManager.Server.exe";
    private const string RuleName = "Inbound Download Manager web UI";

    private static string Decode(FirewallCommand command)
    {
        const string prefix = "-NoProfile -NonInteractive -EncodedCommand ";
        Assert.StartsWith(prefix, command.Arguments);
        return Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[prefix.Length..]));
    }

    // ---- The elevated command ---------------------------------------------------------------

    [Fact]
    [Trait("Platform", "Windows")]
    public void TheAllowCommandIsWindowsPowerShellInSystem32WithFixedArguments()
    {
        var command = WebUiFirewall.BuildAllowCommand(Bridge, 8080, RuleName, alsoPublic: false, System32);

        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", command.FileName);
        Assert.True(WebUiFirewall.IsSystemProgram(command.FileName, System32));
        Assert.Matches(@"^-NoProfile -NonInteractive -EncodedCommand [A-Za-z0-9+/=]+$", command.Arguments);
    }

    [Fact]
    [Trait("Platform", "Windows")]
    public void TheScriptRemovesBlockRulesThenAddsOneAllowRuleForTheProgramPortAndLocalSubnet()
    {
        var script = Decode(WebUiFirewall.BuildAllowCommand(Bridge, 8080, RuleName, alsoPublic: false, System32));

        var expected = string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "try {",
            @"  $program = 'C:\Program Files\Inbound Download Manager\DownloadManager.Server.exe'",
            "  $name = 'Inbound Download Manager web UI'",
            "  Get-NetFirewallApplicationFilter -Program $program -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } | Remove-NetFirewallRule",
            "  Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule",
            "  New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow -Program $program -Protocol TCP -LocalPort 8080 -RemoteAddress LocalSubnet -Profile Private | Out-Null",
            "  exit 0",
            "} catch {",
            "  exit 1",
            "}");
        Assert.Equal(expected, script);
    }

    [Fact]
    [Trait("Platform", "Windows")]
    public void PublicNetworksAreAddedOnlyWhenAsked()
    {
        var withoutPublic = Decode(WebUiFirewall.BuildAllowCommand(Bridge, 9090, RuleName, alsoPublic: false, System32));
        var withPublic = Decode(WebUiFirewall.BuildAllowCommand(Bridge, 9090, RuleName, alsoPublic: true, System32));

        Assert.Contains("-LocalPort 9090 -RemoteAddress LocalSubnet -Profile Private |", withoutPublic);
        Assert.DoesNotContain("Public", withoutPublic);
        Assert.Contains("-Profile Private,Public |", withPublic);
    }

    [Fact]
    [Trait("Platform", "Windows")]
    public void QuotesInAPathCannotEndTheQuotedString()
    {
        var path = @"C:\Users\O'Neil\it’s here\DownloadManager.Server.exe";

        var script = Decode(WebUiFirewall.BuildAllowCommand(path, 8080, RuleName, alsoPublic: false, System32));

        Assert.Contains(@"$program = 'C:\Users\O''Neil\it’’s here\DownloadManager.Server.exe'", script);
        Assert.Equal("'a''b'", WebUiFirewall.Quote("a'b"));
        Assert.Equal("'a‘‘b'", WebUiFirewall.Quote("a‘b"));
    }

    [Theory]
    [InlineData(@"DownloadManager.Server.exe")]
    [InlineData(@"..\DownloadManager.Server.exe")]
    [InlineData(@"\\server\share\DownloadManager.Server.exe")]
    [InlineData(@"C:\Program Files\Inbound\DownloadManager.Host.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("C:\\Program Files\\x\n\\DownloadManager.Server.exe")]
    [InlineData("")]
    [Trait("Platform", "Windows")]
    public void OnlyTheWebUiProgramCanBeNamedInARule(string path)
    {
        Assert.Throws<ArgumentException>(() => WebUiFirewall.BuildAllowCommand(path, 8080, RuleName, false, System32));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    [Trait("Platform", "Windows")]
    public void APortOutsideTheAllowedRangeIsRefused(int port)
    {
        Assert.Throws<ArgumentException>(() => WebUiFirewall.BuildAllowCommand(Bridge, port, RuleName, false, System32));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public void ARuleNeedsAnUsableName(string name)
    {
        Assert.Throws<ArgumentException>(() => WebUiFirewall.BuildAllowCommand(Bridge, 8080, name, false, System32));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", true)]
    [InlineData(@"c:\windows\system32\windowspowershell\v1.0\POWERSHELL.EXE", true)]
    [InlineData(@"C:\Windows\System32\netsh.exe", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(@"powershell.exe", false)]
    [InlineData(@"C:\Program Files\Inbound Download Manager\powershell.exe", false)]
    [InlineData(@"C:\Windows\System32\..\Temp\netsh.exe", false)]
    [InlineData(@"C:\Windows\SysWOW64\netsh.exe", false)]
    [InlineData(@"C:\Windows\System32\DownloadManager.Server.exe", false)]
    [Trait("Platform", "Windows")]
    public void OnlySystemPowerShellAndNetshMayBeStarted(string fileName, bool allowed)
    {
        Assert.Equal(allowed, WebUiFirewall.IsSystemProgram(fileName, System32));
    }

    [Fact]
    [Trait("Platform", "Windows")]
    public void ReadingTheNetworkTypeNeedsNoPermissionAndRunsFromSystem32()
    {
        var command = WebUiFirewall.NetworkProfileCommand(System32);

        Assert.True(WebUiFirewall.IsSystemProgram(command.FileName, System32));
        Assert.Contains("Get-NetConnectionProfile", command.Arguments);
        Assert.DoesNotContain("runas", command.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNetworkTypesAreReadFromTheOutput()
    {
        Assert.Equal([NetworkProfile.Public, NetworkProfile.Private, NetworkProfile.Domain],
            WebUiFirewall.ParseProfiles("Public\r\nPrivate\r\nDomainAuthenticated\r\n"));
        Assert.Empty(WebUiFirewall.ParseProfiles(""));
        Assert.Empty(WebUiFirewall.ParseProfiles("something else\n"));
        Assert.Empty(WebUiFirewall.ParseProfiles(null));
    }

    // ---- The page's checks ---------------------------------------------------------------------

    [Theory]
    [InlineData(1024, true)]
    [InlineData(8080, true)]
    [InlineData(65535, true)]
    [InlineData(1023, false)]
    [InlineData(65536, false)]
    [InlineData(80.5, false)]
    [InlineData(double.NaN, false)]
    public void ThePortIsAWholeNumberFrom1024To65535(double port, bool fine)
    {
        Assert.Equal(fine, WebUiForm.PortProblem(port) is null);
    }

    [Fact]
    public void ThePasswordFollowsTheSignInRulesAndMustBeTypedTwice()
    {
        Assert.NotNull(WebUiForm.PasswordProblem("", ""));
        Assert.NotNull(WebUiForm.PasswordProblem("short", "short"));
        Assert.NotNull(WebUiForm.PasswordProblem(new string('a', 1025), new string('a', 1025)));
        Assert.Equal("The two passwords don't match.", WebUiForm.PasswordProblem("long enough", "long enougH"));
        Assert.Null(WebUiForm.PasswordProblem("long enough", "long enough"));
        Assert.Null(WebUiForm.PasswordProblem(new string('a', 8), new string('a', 8)));
    }

    [Fact]
    public void NamesAreSplitCheckedAndKeptOnceEach()
    {
        Assert.Equal(["nas", "nas.example.com"], WebUiForm.ParseNames("nas, NAS;nas.example.com\n", out var problem));
        Assert.Null(problem);
        Assert.Empty(WebUiForm.ParseNames("  ", out problem)!);

        foreach (var bad in new[] { "http://nas", "nas:8080", "nas/x", "192.168.1.5", "-nas", "na s!", "nas..local" })
        {
            Assert.Null(WebUiForm.ParseNames(bad, out problem));
            Assert.NotNull(problem);
        }

        Assert.Null(WebUiForm.ParseNames("a,b,c,d,e,f,g,h,i", out problem));
        Assert.Contains("at most", problem);
    }

    [Fact]
    public void TheBoxCannotBeTickedWithoutAPassword()
    {
        var saved = new WebUiStatusDto { HasPassword = false };

        var request = WebUiForm.Changes(saved, enabled: true, 8080, "", out var problem);

        Assert.Null(request);
        Assert.Contains("password", problem);
    }

    [Fact]
    public void OnlyWhatChangedIsSent()
    {
        var saved = new WebUiStatusDto { Enabled = false, HasPassword = true, Port = 8080, AlsoReachableAs = ["nas"] };

        Assert.Null(WebUiForm.Changes(saved, false, 8080, "nas", out var problem));
        Assert.Null(problem);

        var on = WebUiForm.Changes(saved, true, 8080, "NAS", out _)!;
        Assert.True(on.Enabled);
        Assert.Null(on.Port);
        Assert.Null(on.AlsoReachableAs);

        var moved = WebUiForm.Changes(saved, false, 9090, "nas, nas.local", out _)!;
        Assert.Null(moved.Enabled);
        Assert.Equal(9090, moved.Port);
        Assert.Equal(["nas", "nas.local"], moved.AlsoReachableAs);

        Assert.Equal([], WebUiForm.Changes(saved, false, 8080, "", out _)!.AlsoReachableAs);
    }

    [Fact]
    public void AnEnabledBoxCanAlwaysBeSwitchedOffEvenIfThePasswordIsGone()
    {
        var saved = new WebUiStatusDto { Enabled = true, HasPassword = false };

        var request = WebUiForm.Changes(saved, false, 8080, "", out var problem);

        Assert.Null(problem);
        Assert.False(request!.Enabled);
    }

    [Fact]
    public void TheAddressesToTypeAreTheHostsPlusTheNamesAndNothingWhileItIsOff()
    {
        var running = new WebUiStatusDto
        {
            State = WebUiStates.Running, Port = 8080, AlsoReachableAs = ["nas"],
            Addresses = ["http://192.168.1.20:8080/"], Firewall = WebUiFirewallStates.Ready,
        };

        Assert.Equal(["http://192.168.1.20:8080/", "http://nas:8080/"], WebUiForm.AddressesToType(running));
        Assert.Empty(WebUiForm.AddressesToType(new WebUiStatusDto { State = WebUiStates.Off }));
        Assert.Equal(["http://localhost:8080/"], WebUiForm.AddressesToType(new WebUiStatusDto { State = WebUiStates.Running, LocalOnly = true }));
    }

    [Fact]
    public void TheStateIsToldInPlainWords()
    {
        Assert.StartsWith("Off", WebUiForm.StateText(new WebUiStatusDto()).Text);
        Assert.Contains("only this PC", WebUiForm.StateText(new WebUiStatusDto { State = WebUiStates.Running, LocalOnly = true }).Text);
        var (failed, problem) = WebUiForm.StateText(new WebUiStatusDto { State = WebUiStates.Failed, Error = "Port 8080 is in use." });
        Assert.True(problem);
        Assert.Contains("Port 8080 is in use.", failed);
        Assert.Contains("isn't set to allow", WebUiForm.FirewallText(new WebUiStatusDto { Firewall = WebUiFirewallStates.Missing }));
    }

    [Fact]
    public void TheNetworkTypeLineSaysWhatWasRead()
    {
        Assert.Equal("Network type: Public.", WebUiForm.ProfileText([NetworkProfile.Public, NetworkProfile.Public]));
        Assert.Equal("Network type: Private, Public.", WebUiForm.ProfileText([NetworkProfile.Private, NetworkProfile.Public]));
        Assert.Contains("couldn't be read", WebUiForm.ProfileText(null));
        Assert.Contains("isn't connected", WebUiForm.ProfileText([]));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/?ticket=abc", 8080, true)]
    [InlineData("http://localhost:8080/", 8080, true)]
    [InlineData("http://127.0.0.1:9090/", 8080, false)]
    [InlineData("https://127.0.0.1:8080/", 8080, false)]
    [InlineData("http://evil.example:8080/", 8080, false)]
    [InlineData("http://user:pw@127.0.0.1:8080/", 8080, false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", 8080, false)]
    [InlineData("", 8080, false)]
    public void OnlyTheHostsOwnSignInLinkOnThisPcIsOpened(string url, int port, bool fine)
    {
        Assert.Equal(fine, WebUiForm.IsLocalSignInUrl(url, port));
    }

    // ---- The calls to the Host -----------------------------------------------------------------

    [Fact]
    public async Task TheStatusIsReadFromTheHost()
    {
        var host = new WebUiHost();
        host.Answer(WebUiMessageTypes.Get, "webui.status", new WebUiStatusDto
        {
            Enabled = true, Port = 8081, HasPassword = true, State = WebUiStates.Running, Firewall = WebUiFirewallStates.Missing,
            LocalOnly = true, BridgePath = Bridge, RuleName = RuleName, AlsoReachableAs = ["nas"],
        });
        await using var client = host.NewClient();

        var result = await client.GetWebUiAsync();

        Assert.True(result.Ok);
        Assert.Equal(8081, result.Status!.Port);
        Assert.True(result.Status.LocalOnly);
        Assert.Equal(WebUiFirewallStates.Missing, result.Status.Firewall);
        Assert.Equal(Bridge, result.Status.BridgePath);
        Assert.Equal(["nas"], result.Status.AlsoReachableAs);
    }

    [Fact]
    public async Task OnlyTheChangedFieldsAreSentAndThePasswordGoesInItsOwnMessage()
    {
        var host = new WebUiHost();
        host.Answer(WebUiMessageTypes.Set, "webui.status", new WebUiStatusDto { Port = 9000 });
        host.Answer(WebUiMessageTypes.SetPassword, "webui.status", new WebUiStatusDto { HasPassword = true });
        await using var client = host.NewClient();

        await client.SetWebUiAsync(new WebUiSetRequest { Port = 9000 });
        await client.SetWebUiPasswordAsync("a long password");

        var set = host.LastPayload(WebUiMessageTypes.Set)!.Value;
        Assert.Equal(9000, set.GetProperty("port").GetInt32());
        Assert.False(set.TryGetProperty("enabled", out _));
        Assert.False(set.TryGetProperty("alsoReachableAs", out _));
        Assert.Equal("a long password", host.LastPayload(WebUiMessageTypes.SetPassword)!.Value.GetProperty("password").GetString());
    }

    [Fact]
    public async Task ARefusalComesBackInTheHostsWords()
    {
        var host = new WebUiHost();
        host.Answer(WebUiMessageTypes.Set, MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Set a password first." });
        await using var client = host.NewClient();

        var result = await client.SetWebUiAsync(new WebUiSetRequest { Enabled = true });

        Assert.False(result.Ok);
        Assert.Equal("Set a password first.", result.Error);
        Assert.Null(result.Status);
    }

    [Fact]
    public async Task AHostWithoutTheWebUiSaysSoInPlainWords()
    {
        var host = new WebUiHost();
        host.Answer(WebUiMessageTypes.Get, MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." });
        await using var client = host.NewClient();

        var result = await client.GetWebUiAsync();

        Assert.Equal(RemoteText.HostDoesNotOffer, result.Error);
    }

    [Fact]
    public async Task ASuccessWithoutAStatusHasNoErrorAndNoStatus()
    {
        var host = new WebUiHost();
        host.Answer(WebUiMessageTypes.SetPassword, MessageTypes.HostActionResult, new HostActionResult { Ok = true });
        await using var client = host.NewClient();

        var result = await client.SetWebUiPasswordAsync("a long password");

        Assert.True(result.Ok);
        Assert.Null(result.Status);
    }

    [Fact]
    public async Task ATicketCarriesTheSignInLink()
    {
        var host = new WebUiHost();
        host.Answer(WebUiMessageTypes.OpenTicket, "webui.ticket", new WebUiTicketDto { Url = "http://127.0.0.1:8080/?ticket=t" });
        await using var client = host.NewClient();

        var ticket = await client.OpenWebUiTicketAsync();

        Assert.True(ticket.Ok);
        Assert.Equal("http://127.0.0.1:8080/?ticket=t", ticket.Url);
    }

    [Fact]
    public async Task ATicketTheHostRefusesIsNotALink()
    {
        var host = new WebUiHost();
        host.Answer(WebUiMessageTypes.OpenTicket, MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "The web UI isn't running." });
        await using var client = host.NewClient();

        var ticket = await client.OpenWebUiTicketAsync();

        Assert.False(ticket.Ok);
        Assert.Equal("The web UI isn't running.", ticket.Error);
    }

    [Fact]
    public async Task AChangeFromTheHostReachesThePageOnTheUiThread()
    {
        var host = new WebUiHost();
        await using var client = host.NewClient();
        var heard = new List<WebUiStatusDto>();
        client.WebUiChanged += heard.Add;
        client.Start();
        var pipe = await host.FirstConnectionAsync();

        pipe.Push(IpcMessage.Create(WebUiMessageTypes.Changed, new WebUiStatusDto { State = WebUiStates.Failed, Error = "Port in use." }));

        await host.UntilAsync(() => { host.Flush(); return heard.Count == 1; });
        Assert.Equal("Port in use.", heard[0].Error);
    }

    private sealed class WebUiHost
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<string, Func<IpcMessage, IpcMessage>> _answers = [];
        private readonly Dictionary<string, JsonElement?> _payloads = [];
        private readonly Queue<Action> _ui = new();
        private readonly TaskCompletionSource<Pipe> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RemoteClient NewClient() => new(Post, Connect, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        public Task<Pipe> FirstConnectionAsync() => _first.Task;

        public void Answer<T>(string type, string replyType, T reply)
        {
            lock (_sync) _answers[type] = request => IpcMessage.Create(replyType, reply, request.Id);
        }

        public JsonElement? LastPayload(string type)
        {
            lock (_sync) return _payloads[type];
        }

        public void Flush()
        {
            while (true)
            {
                Action action;
                lock (_sync)
                {
                    if (_ui.Count == 0) return;
                    action = _ui.Dequeue();
                }

                action();
            }
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("It never happened.");
                await Task.Delay(5);
            }
        }

        private void Post(Action action)
        {
            lock (_sync) _ui.Enqueue(action);
        }

        private Task<IRemotePipe> Connect(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var pipe = new Pipe(this);
            pipe.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { ProtocolVersion = Protocol.Version, Capabilities = ["jobs"] }));
            pipe.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto()));
            _first.TrySetResult(pipe);
            return Task.FromResult<IRemotePipe>(pipe);
        }

        private IpcMessage Receive(IpcMessage request)
        {
            lock (_sync)
            {
                _payloads[request.Type] = request.Payload;
                if (_answers.TryGetValue(request.Type, out var answer)) return answer(request);
            }

            return IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }, request.Id);
        }

        public sealed class Pipe(WebUiHost host) : IRemotePipe
        {
            private readonly Channel<IpcMessage?> _toClient = Channel.CreateUnbounded<IpcMessage?>();

            public void Push(IpcMessage message) => _toClient.Writer.TryWrite(message);

            public Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Push(host.Receive(message));
                return Task.CompletedTask;
            }

            public async Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken)
                => await _toClient.Reader.ReadAsync(cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
