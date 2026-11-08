using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownloadManager.Contracts;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// A fixed script of commands to the real Host over its real pipe, with every frame it sends back written down
/// after the parts that change from run to run (ids, times, paths, the version) are replaced. Moving the command
/// handling out of the pipe server (D1a) must not change a single frame: the script is run on the code before the
/// move and after it, and the two files must be equal.
/// <para>
/// Every run compares its frames with the committed <c>GoldenFrames\host-pipe-session.txt</c>, recorded on the code
/// before the move, so a change to what an existing client sees fails here. A change meant to alter the frames
/// rewrites that file with <c>GOLDEN_FRAMES_UPDATE=1</c> and shows the difference in its commit.
/// <c>GOLDEN_FRAMES_BASELINE</c> names another file to compare with instead. Each run's frames also go to
/// <c>artifacts\d1a\frames-&lt;label&gt;.txt</c>, with the label from <c>GOLDEN_FRAMES_LABEL</c> (default <c>latest</c>).
/// </para>
/// </summary>
[Trait("Platform", "Windows")]
public sealed partial class HostGoldenFramesTests : IAsyncLifetime
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private TestServerHost _server = null!;

    public async Task InitializeAsync() => _server = await TestServerHost.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task AScriptedSessionSendsTheSameFrames()
    {
        await using var host = await SandboxHost.StartAsync(_server.TorBox.ApiBase);
        // The setup's settings change announces packages after a debounce. Connecting after it keeps that frame out.
        await Task.Delay(Settle);
        var frames = new List<string>();
        var connection = await HostConnection.OpenAsync(host.PipeName, frame =>
        {
            lock (frames) frames.Add(frame);
        });

        async Task Ask(string type, object payload)
        {
            await connection.RequestAsync(type, payload);
            // Debounced broadcasts (packages) land before the next command, so the order is the same every run.
            await Task.Delay(Settle);
        }

        await using (connection)
        {
            await Ask(MessageTypes.HostPing, new { });
            await Ask(MessageTypes.HostStatus, new { });
            await Ask(MessageTypes.SettingsGet, new { });
            await Ask(MessageTypes.JobsQuery, new { });
            await Ask(MessageTypes.StatisticsGet, new { });
            await Ask("nope.nothing", new { });

            // "later": nothing goes out, so the address is never contacted.
            var added = await connection.RequestAsync(MessageTypes.JobAdd, new AddJobRequest
            {
                Url = "http://127.0.0.1:9/golden/file.bin",
                DestinationDirectory = host.Destination,
                StartMode = "later",
                Description = "golden",
            });
            await Task.Delay(Settle);
            var jobId = added.GetPayload<AddJobResponse>().Job!.Id;
            await Ask(MessageTypes.JobUpdate, new JobUpdateRequest { JobId = jobId, Description = "golden, changed" });
            await Ask(MessageTypes.JobSegments, new JobActionRequest { JobId = jobId });
            await Ask(MessageTypes.JobConnections, new JobActionRequest { JobId = jobId });
            await Ask(MessageTypes.JobPause, new JobActionRequest { JobId = jobId });
            await Ask(MessageTypes.JobRemove, new JobActionRequest { JobId = jobId });
            await Ask(MessageTypes.JobPause, new JobActionRequest { JobId = Guid.Empty });

            // Through the lookahead (IpcOperationLifetime) path.
            await Ask(MessageTypes.JobRefreshUrl, new JobRefreshUrlRequest { JobId = Guid.NewGuid(), Url = "http://127.0.0.1:9/other.bin" });

            await Ask(MessageTypes.HandoffRequest, new HandoffRequest { RequestId = "r1" });
            await Ask(MessageTypes.HandoffRequest, new HandoffRequest { RequestId = "r2", ExtensionId = "notours", Url = "http://127.0.0.1:9/h.bin" });
            await Ask(MessageTypes.HostCleanOrphans, new CleanOrphansRequest { Delete = false });

            // The updater's commands reach the service only where the platform has an updater. A test build is not an
            // installed one, so each stops before the network, and the failed download and install each broadcast
            // update.progress.
            await Ask(MessageTypes.UpdateCheck, new { });
            await Ask(MessageTypes.UpdateDownload, new { });
            await Ask(MessageTypes.UpdateInstall, new UpdateInstallRequest());
            await Ask(MessageTypes.UpdateDefer, new UpdateDeferRequest { Version = "9.9.9-golden" });

            var settings = (await connection.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings;
            await Task.Delay(Settle);
            settings.MaxConcurrentDownloads = settings.MaxConcurrentDownloads == 3 ? 4 : 3;
            await Ask(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });

            await Ask(MessageTypes.RemoteAccountGet, new { });
            await Ask(MessageTypes.RemoteList, new { });
            await Ask(MessageTypes.RemoteSelect, new { });
            await Ask(ArchiveMessageTypes.SettingsGet, new { });
            await Ask(ArchiveMessageTypes.List, new { });
            await Ask(ArchiveMessageTypes.Retry, new ArchiveActionRequest { SetId = Guid.NewGuid() });

            // A client on another protocol version is told so and the connection ends.
            frames.AddRange(await WrongVersionSessionAsync(host.PipeName));

            await Ask(MessageTypes.HostShutdown, new { });
            // host.exiting follows the reply on the same connection.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (!connection.Events.Any(e => e.Type == MessageTypes.HostExiting) && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(50);
        }

        List<string> captured;
        lock (frames) captured = frames.ToList();
        var normalised = Normalise(captured, host);
        Assert.Contains(normalised, line => line.Contains("\"type\":\"server.hello\"", StringComparison.Ordinal));
        Assert.Contains(normalised, line => line.Contains("\"type\":\"host.exiting\"", StringComparison.Ordinal));

        var directory = Path.Combine(RepositoryRoot(), "artifacts", "d1a");
        Directory.CreateDirectory(directory);
        var label = Environment.GetEnvironmentVariable("GOLDEN_FRAMES_LABEL");
        var output = Path.Combine(directory, $"frames-{(string.IsNullOrWhiteSpace(label) ? "latest" : label)}.txt");
        await File.WriteAllLinesAsync(output, normalised, new UTF8Encoding(false));

        var committed = Path.Combine(RepositoryRoot(), "tests", "DownloadManager.Integration.Tests", "GoldenFrames", "host-pipe-session.txt");
        if (Environment.GetEnvironmentVariable("GOLDEN_FRAMES_UPDATE") == "1")
        {
            await File.WriteAllLinesAsync(committed, normalised, new UTF8Encoding(false));
            return;
        }

        var baseline = Environment.GetEnvironmentVariable("GOLDEN_FRAMES_BASELINE") is { Length: > 0 } other ? other : committed;
        var expected = await File.ReadAllLinesAsync(baseline);
        // Line by line first, so a failure names the first frame that differs rather than only the counts.
        for (var i = 0; i < Math.Min(expected.Length, normalised.Count); i++) Assert.Equal(expected[i], normalised[i]);
        Assert.Equal(expected.Length, normalised.Count);
    }

    /// <summary>Its own raw connection, because <see cref="HostConnection"/> always speaks the current version.</summary>
    private static async Task<List<string>> WrongVersionSessionAsync(string pipeName)
    {
        var frames = new List<string>();
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<IpcMessage?> Read()
        {
            var message = await IpcFraming.ReadAsync(pipe, wait.Token);
            if (message is not null) frames.Add(SandboxHost.Unescaped(JsonSerializer.Serialize(message, IpcJson.Options)));
            return message;
        }

        await Read();
        await Read();
        await IpcFraming.WriteAsync(pipe, IpcMessage.Create(MessageTypes.HostPing, new { }) with { V = 99 }, wait.Token);
        while (await Read() is not null) { }
        frames.Add("<eof>");
        return frames;
    }

    private static List<string> Normalise(IEnumerable<string> frames, SandboxHost host)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var frame in frames)
        {
            var text = frame;
            // The sandbox first: it lies under the repository, and the bundled tools' paths under the repository too.
            foreach (var (path, name) in new[] { (host.Root, "<root>"), (host.Root.Replace('\\', '/'), "<root>"), (RepositoryRoot(), "<repo>") })
            {
                text = text.Replace(path.Replace("\\", "\\\\"), name, StringComparison.OrdinalIgnoreCase);
                text = text.Replace(path, name, StringComparison.OrdinalIgnoreCase);
            }

            text = text.Replace(host.Instance, "<instance>", StringComparison.Ordinal);
            text = text.Replace($"\"hostVersion\":\"{Protocol.HostVersion}\"", "\"hostVersion\":\"<version>\"", StringComparison.Ordinal);
            text = Guids().Replace(text, match =>
            {
                if (match.Value.Trim('-').All(c => c is '0' or '-')) return match.Value;
                if (!ids.TryGetValue(match.Value, out var name)) ids[match.Value] = name = $"<id{ids.Count + 1}>";
                return name;
            });
            text = Times().Replace(text, "<time>");
            // Whether this PC has the browser bridge registered is the machine's state, not the protocol's (TEST.3).
            text = text.Replace("\"nativeHostRegistered\":true", "\"nativeHostRegistered\":<bool>", StringComparison.Ordinal)
                .Replace("\"nativeHostRegistered\":false", "\"nativeHostRegistered\":<bool>", StringComparison.Ordinal);
            text = BrowserIntegration().Replace(text, "\"browserIntegration\":\"<state>\"");
            result.Add(text);
        }

        return result;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx")))
            directory = directory.Parent ?? throw new InvalidOperationException("Could not find the repository root.");
        return directory.FullName;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}")]
    private static partial Regex Guids();

    [GeneratedRegex("\"browserIntegration\":\"[^\"]*\"")]
    private static partial Regex BrowserIntegration();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?")]
    private static partial Regex Times();
}
