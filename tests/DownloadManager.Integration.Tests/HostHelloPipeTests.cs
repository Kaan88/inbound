using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// PERF.7: the Host's hello pipe, where a client says first whether it wants the snapshot and the events, on the real
/// Host in a sandbox profile, and the CLI and the native host that use it. The main pipe is what every other test here
/// connects to (<see cref="HostConnection.OpenAsync"/> asserts its snapshot), so it is not repeated.
/// </summary>
public sealed class HostHelloPipeTests
{
    private const string NoApi = "https://example.invalid/v1/api/";

    private static async Task<NamedPipeClientStream> HelloPipeAsync(SandboxHost host)
    {
        var pipe = new NamedPipeClientStream(".", host.PipeName + "-hello", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        return pipe;
    }

    private static async Task<IpcMessage?> ReadAsync(Stream pipe)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return await IpcFraming.ReadAsync(pipe, wait.Token);
    }

    [Trait("Platform", "Windows")]
    [Fact]
    public async Task Asking_for_nothing_gets_the_hello_and_then_only_replies()
    {
        await using var host = await SandboxHost.StartAsync(NoApi);
        await using var other = await host.ConnectAsync();
        await using var pipe = await HelloPipeAsync(host);

        await IpcFraming.WriteAsync(pipe, IpcMessage.Create(MessageTypes.ClientHello, new ClientHelloDto { Snapshot = false, Events = false }));
        var hello = await ReadAsync(pipe);
        Assert.Equal(MessageTypes.ServerHello, hello!.Type);
        Assert.Equal(Protocol.Version, hello.V);
        Assert.Equal(["jobs", "settings", "handoff", "tray", "status"], hello.GetPayload<HostHelloDto>().Capabilities.Take(5));

        // A job added elsewhere is broadcast, but not to this connection: the next frame here is the reply.
        var added = await other.RequestAsync(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = "http://127.0.0.1:9/quiet.bin",
            DestinationDirectory = host.Destination,
            StartMode = "later",
        });
        Assert.True(added.GetPayload<AddJobResponse>().Accepted);
        var query = IpcMessage.Create(MessageTypes.JobsQuery, new { });
        await IpcFraming.WriteAsync(pipe, query);
        var reply = await ReadAsync(pipe);
        Assert.Equal(query.Id, reply!.ReplyTo);
        Assert.Equal(MessageTypes.JobsSnapshot, reply.Type);
        Assert.Single(reply.GetPayload<SnapshotDto>().Jobs);
    }

    [Trait("Platform", "Windows")]
    [Fact]
    public async Task Asking_for_events_only_gets_the_events_but_no_snapshot()
    {
        await using var host = await SandboxHost.StartAsync(NoApi);
        await using var other = await host.ConnectAsync();
        await using var pipe = await HelloPipeAsync(host);

        await IpcFraming.WriteAsync(pipe, IpcMessage.Create(MessageTypes.ClientHello, new ClientHelloDto { Snapshot = false }));
        Assert.Equal(MessageTypes.ServerHello, (await ReadAsync(pipe))!.Type);
        await other.RequestAsync(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = "http://127.0.0.1:9/seen.bin",
            DestinationDirectory = host.Destination,
            StartMode = "later",
        });

        // Other broadcasts (groups.changed) may come too; never the snapshot.
        while (true)
        {
            var next = await ReadAsync(pipe) ?? throw new IOException("The Host closed the connection.");
            Assert.NotEqual(MessageTypes.JobsSnapshot, next.Type);
            if (next.Type == MessageTypes.JobEvent && next.GetPayload<JobEventDto>().Job.FileName == "seen.bin") break;
        }
    }

    [Trait("Platform", "Windows")]
    [Fact]
    public async Task An_empty_hello_gets_the_snapshot_as_on_the_main_pipe()
    {
        await using var host = await SandboxHost.StartAsync(NoApi);
        await using var pipe = await HelloPipeAsync(host);

        await IpcFraming.WriteAsync(pipe, IpcMessage.Create(MessageTypes.ClientHello, new { }));
        Assert.Equal(MessageTypes.ServerHello, (await ReadAsync(pipe))!.Type);
        Assert.Equal(MessageTypes.JobsSnapshot, (await ReadAsync(pipe))!.Type);
    }

    [Trait("Platform", "Windows")]
    [Fact]
    public async Task Anything_but_a_hello_first_is_closed_and_another_version_is_told_so()
    {
        await using var host = await SandboxHost.StartAsync(NoApi);
        await using (var pipe = await HelloPipeAsync(host))
        {
            await IpcFraming.WriteAsync(pipe, IpcMessage.Create(MessageTypes.HostPing, new { }));
            Assert.Null(await ReadAsync(pipe));
        }

        await using (var pipe = await HelloPipeAsync(host))
        {
            await IpcFraming.WriteAsync(pipe, IpcMessage.Create(MessageTypes.ClientHello, new ClientHelloDto()) with { V = Protocol.Version + 1 });
            Assert.Equal(MessageTypes.ServerHello, (await ReadAsync(pipe))!.Type);
            Assert.Equal(MessageTypes.ServerIncompatible, (await ReadAsync(pipe))!.Type);
            Assert.Null(await ReadAsync(pipe));
        }

        // And the Host goes on answering.
        await using var after = await host.ConnectAsync();
        Assert.True((await after.AskAsync<HostActionResult>(MessageTypes.HostPing, new { })).Ok);
    }

    [Trait("Platform", "Windows")]
    [Fact]
    public async Task The_cli_still_adds_lists_and_reports_through_the_hello_pipe()
    {
        await using var host = await SandboxHost.StartAsync(NoApi);

        var add = await host.RunCliAsync(null, "add", "http://127.0.0.1:9/cli.bin", "--dest", host.Destination, "--start", "later");
        Assert.True(add.ExitCode == 0, add.All);
        var list = await host.RunCliAsync(null, "list", "--json");
        Assert.True(list.ExitCode == 0, list.All);
        var snapshot = JsonSerializer.Deserialize<SnapshotDto>(list.Output, IpcJson.Options)!;
        Assert.Equal("cli.bin", Assert.Single(snapshot.Jobs).FileName);
        var doctor = await host.RunCliAsync(null, "doctor");
        Assert.True(doctor.ExitCode == 0, doctor.All);
        Assert.Contains("1 total", doctor.Output, StringComparison.Ordinal);
    }

    [Trait("Platform", "Windows")]
    [Fact]
    public async Task The_native_host_forwards_no_snapshot_but_still_the_events()
    {
        await using var host = await SandboxHost.StartAsync(NoApi);
        await using var other = await host.ConnectAsync();
        using var native = host.StartNativeHost();
        try
        {
            var input = native.StandardInput.BaseStream;
            var output = native.StandardOutput.BaseStream;
            var ping = IpcMessage.Create(MessageTypes.HostPing, new { });
            await IpcFraming.WriteAsync(input, ping);
            var before = new List<string>();
            while (true)
            {
                var frame = await ReadAsync(output) ?? throw new IOException("The native host closed.");
                if (frame.ReplyTo == ping.Id) break;
                before.Add(frame.Type);
            }

            Assert.DoesNotContain(MessageTypes.JobsSnapshot, before);

            await other.RequestAsync(MessageTypes.JobAdd, new AddJobRequest
            {
                Url = "http://127.0.0.1:9/browser.bin",
                DestinationDirectory = host.Destination,
                StartMode = "later",
            });
            while (true)
            {
                var frame = await ReadAsync(output) ?? throw new IOException("The native host closed.");
                Assert.NotEqual(MessageTypes.JobsSnapshot, frame.Type);
                if (frame.Type == MessageTypes.JobEvent && frame.GetPayload<JobEventDto>().Job.FileName == "browser.bin") break;
            }
        }
        finally
        {
            if (!native.HasExited) native.Kill(entireProcessTree: true);
            await native.WaitForExitAsync();
        }
    }

    /// <summary>
    /// A Host from before the hello pipe: only the main pipe, which sends the hello and the snapshot as soon as a
    /// client connects. Played here by the test, under a test instance's pipe name, so the CLI's fallback is what runs.
    /// </summary>
    [Trait("Platform", "Windows")]
    [Fact]
    public async Task The_cli_falls_back_to_the_main_pipe_of_a_host_without_the_hello_pipe()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", "oldhost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var instance = "t" + Guid.NewGuid().ToString("N");
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}\nInstance:{instance}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 8)).ToLowerInvariant();
        try
        {
            await using var server = new NamedPipeServerStream($"DownloadManager-{key}", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var serving = Task.Run(async () =>
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await server.WaitForConnectionAsync(wait.Token);
                await IpcFraming.WriteAsync(server, IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto
                {
                    HostVersion = "0.6.1", ProtocolVersion = Protocol.Version, Capabilities = ["jobs", "settings", "handoff", "tray", "status"],
                }), wait.Token);
                await IpcFraming.WriteAsync(server, IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto()), wait.Token);
                var request = await IpcFraming.ReadAsync(server, wait.Token);
                Assert.Equal(MessageTypes.HostStatus, request!.Type);
                await IpcFraming.WriteAsync(server, IpcMessage.Create(MessageTypes.HostStatusResult,
                    new HostStatusDto { HostVersion = "0.6.1", ProtocolVersion = Protocol.Version, TotalJobs = 42 }, request.Id), wait.Token);
            });

            var info = new ProcessStartInfo(CliExecutable())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = root,
            };
            foreach (var name in info.Environment.Keys.Where(n => n.StartsWith("DOWNLOADMANAGER_", StringComparison.OrdinalIgnoreCase)).ToList())
                info.Environment.Remove(name);
            info.Environment["DOWNLOADMANAGER_INSTANCE"] = instance;
            info.Environment["DOWNLOADMANAGER_DATA"] = root;
            info.ArgumentList.Add("doctor");
            info.ArgumentList.Add("--no-start");
            using var cli = Process.Start(info)!;
            cli.StandardInput.Close();
            var stdout = cli.StandardOutput.ReadToEndAsync();
            var stderr = cli.StandardError.ReadToEndAsync();
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await cli.WaitForExitAsync(exit.Token);
            await serving;
            Assert.True(cli.ExitCode == 0, await stdout + await stderr);
            Assert.Contains("42 total", await stdout, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    private static string CliExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent!.Name;
        while (!File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx")))
            directory = directory.Parent ?? throw new InvalidOperationException("Could not find the repository root.");
        var path = Path.Combine(directory.FullName, "src", "DownloadManager.Cli", "bin", configuration, "net10.0", "DownloadManager.Cli.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException($"The CLI has not been built: {path}");
    }
}
