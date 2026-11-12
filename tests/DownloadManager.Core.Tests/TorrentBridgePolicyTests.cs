using System.Reflection;
using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Core.Tests;

/// <summary>
/// T1: the browser extension never handles torrents (docs/plans/native-torrent-roadmap.md, rule 5), so no torrent
/// message crosses a browser bridge either way, through the native host or a paired server.
/// </summary>
public sealed class TorrentBridgePolicyTests
{
    [Fact]
    public void NoBrowserBridgeCarriesATorrentMessageEitherWay()
    {
        var types = typeof(TorrentMessageTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.Contains("torrent.inspect", types);
        Assert.Contains("torrent.add", types);
        Assert.Contains("torrent.setFiles", types);
        Assert.Contains("torrent.files", types);
        Assert.Contains("torrent.addResult", types);
        // T2
        Assert.Contains("torrent.peers", types);
        Assert.Contains("torrent.trackers", types);
        Assert.Contains("torrent.pieces", types);
        Assert.Contains("torrent.seed", types);
        Assert.Contains("torrent.piecesResult", types);
        Assert.All(new[] { "torrent.peers", "torrent.trackers", "torrent.pieces", "torrent.seed" }, type => Assert.True(TorrentMessageTypes.IsRequest(type)));
        foreach (var role in new[] { BridgeRole.BrowserExtension, BridgeRole.ServerExtension })
        {
            Assert.All(types, type =>
            {
                Assert.False(NativeBridgePolicy.AllowFrom(role, type), $"{type} reached the Host from the browser ({role}).");
                Assert.False(NativeBridgePolicy.AllowFrom(role, IpcMessage.Create(type)), $"{type} reached the Host from the browser ({role}).");
                Assert.False(NativeBridgePolicy.AllowTo(role, type), $"{type} reached the browser from the Host ({role}).");
                Assert.Null(NativeBridgePolicy.To(role, IpcMessage.Create(type)));
            });
        }
    }

    private static readonly string Hash = new('a', 40);
    private static readonly string FullMagnet = $"magnet:?xt=urn:btih:{Hash}&dn=Album&tr=udp%3A%2F%2Ftracker.test%3A1337&tr=https%3A%2F%2Fpasskey.test%2Fsecret%2Fannounce";

    private static JobDto TorrentJob(bool awaitingChoice, string state = "paused", bool canResume = true) => new()
    {
        Id = Guid.NewGuid(),
        Url = FullMagnet,
        FinalUrl = FullMagnet,
        FileName = "Album",
        State = state,
        CanResume = canResume,
        Torrent = new TorrentDto { InfoHash = Hash, Name = "Album", HasMetadata = true, AwaitingFileChoice = awaitingChoice },
    };

    private static JobDto PlainJob() => new()
    {
        Id = Guid.NewGuid(), Url = "https://files.test/a.zip?token=1", FinalUrl = "https://cdn.test/a.zip", FileName = "a.zip",
        State = "paused", CanResume = true,
    };

    private static JsonElement JobOf(IpcMessage message) => message.Payload!.Value.GetProperty("job");

    public static TheoryData<BridgeRole> Roles => new() { BridgeRole.BrowserExtension, BridgeRole.ServerExtension };

    [Theory]
    [MemberData(nameof(Roles))]
    public void ASnapshotGivesABrowserATorrentsShortAddressAndNoResumeWhileItWaitsForItsFileChoice(BridgeRole role)
    {
        var snapshot = new SnapshotDto
        {
            Jobs = [TorrentJob(awaitingChoice: true), TorrentJob(awaitingChoice: false), PlainJob()],
            Settings = new SettingsDto { DefaultDownloadDirectory = @"C:\secret-folder" },
        };

        var shown = NativeBridgePolicy.To(role, IpcMessage.Create(MessageTypes.JobsSnapshot, snapshot))!;

        var text = shown.Payload!.Value.GetRawText();
        Assert.DoesNotContain("tracker.test", text);
        Assert.DoesNotContain("passkey", text);
        Assert.DoesNotContain("secret-folder", text);
        var jobs = shown.GetPayload<SnapshotDto>().Jobs;
        var (waiting, running, plain) = (jobs[0], jobs[1], jobs[2]);
        Assert.Equal($"magnet:?xt=urn:btih:{Hash}", waiting.Url);
        Assert.Null(waiting.FinalUrl);
        Assert.False(waiting.CanResume);
        Assert.Equal($"magnet:?xt=urn:btih:{Hash}", running.Url);
        Assert.True(running.CanResume); // a torrent paused by the user resumes as usual
        // An ordinary download passes as it was.
        Assert.Equal(("https://files.test/a.zip?token=1", "https://cdn.test/a.zip", true), (plain.Url, plain.FinalUrl, plain.CanResume));
        var raw = shown.Payload!.Value.GetProperty("jobs");
        Assert.True(raw[0].GetProperty(NativeBridgePolicy.AppManagedProperty).GetBoolean());
        Assert.True(raw[1].GetProperty(NativeBridgePolicy.AppManagedProperty).GetBoolean());
        Assert.False(raw[2].TryGetProperty(NativeBridgePolicy.AppManagedProperty, out _));
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void AnEventAnActionsAnswerAndAHandoffAckAreShownTheSameWay(BridgeRole role)
    {
        var messages = new[]
        {
            IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto { Job = TorrentJob(awaitingChoice: true), EventType = "updated" }),
            IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = true, Job = TorrentJob(awaitingChoice: true) }),
            IpcMessage.Create(MessageTypes.HandoffAck, new HandoffAck { Accepted = true, Job = TorrentJob(awaitingChoice: true) }),
        };

        foreach (var message in messages)
        {
            var shown = NativeBridgePolicy.To(role, message)!;
            Assert.DoesNotContain("tracker.test", shown.Payload!.Value.GetRawText());
            var job = JobOf(shown);
            Assert.Equal($"magnet:?xt=urn:btih:{Hash}", job.GetProperty("url").GetString());
            Assert.Equal(JsonValueKind.Null, job.GetProperty("finalUrl").ValueKind);
            Assert.False(job.GetProperty("canResume").GetBoolean());
            Assert.True(job.GetProperty(NativeBridgePolicy.AppManagedProperty).GetBoolean());
        }
    }

    [Fact]
    public void AnOrdinaryDownloadsEventPassesUnchanged()
    {
        var message = IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto { Job = PlainJob(), EventType = "progress" });
        Assert.Same(message, NativeBridgePolicy.To(BridgeRole.BrowserExtension, message));
    }

    [Fact]
    public void EverySpellingOfTheAddressIsRewrittenAndAnUnknownHashLeavesNoAddress()
    {
        // The Host reads names without regard to case; a second spelling must not carry the trackers through.
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["job"] = new Dictionary<string, object?>
            {
                ["url"] = FullMagnet, ["URL"] = FullMagnet, ["canResume"] = true, ["state"] = "paused",
                ["torrent"] = new Dictionary<string, object?> { ["infoHash"] = "", ["awaitingFileChoice"] = true },
            },
        });
        var message = new IpcMessage(Protocol.Version, MessageTypes.JobEvent, "1", null, payload);

        var job = JobOf(NativeBridgePolicy.To(BridgeRole.BrowserExtension, message)!);

        Assert.Equal(string.Empty, job.GetProperty("url").GetString());
        Assert.Equal(string.Empty, job.GetProperty("URL").GetString());
        Assert.False(job.GetProperty("canResume").GetBoolean());
    }

    // Real texts the Host gives a torrent job (TorrentJobs.cs, DownloadManagerService.cs): words the extension may not show.
    private const string HostFailure = "Torrents are turned off. Turn them on in Options > Torrents to resume this download.";
    private const string HostRefusal = "A torrent's files can't be moved or renamed.";

    [Theory]
    [MemberData(nameof(Roles))]
    public void ATorrentsFailureNoteAndDetailsReachTheBrowserWithoutItsWords(BridgeRole role)
    {
        var failed = TorrentJob(awaitingChoice: false, state: "failed");
        failed.Failure = new FailureDto { Kind = "unknown", Message = HostFailure, IsRetryable = false, IsResumable = true };
        failed.StatusNote = "The torrent stopped: no peers";
        var snapshot = new SnapshotDto { Jobs = [failed, PlainJob()] };
        snapshot.Jobs[1].Failure = new FailureDto { Kind = "network", Message = "The server closed the connection." };
        var messages = new[]
        {
            IpcMessage.Create(MessageTypes.JobsSnapshot, snapshot),
            IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto { Job = failed, EventType = "failed" }),
            IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = false, Error = HostRefusal, Job = failed }),
        };

        foreach (var message in messages)
        {
            var shown = NativeBridgePolicy.To(role, message)!;
            var text = shown.Payload!.Value.GetRawText();
            Assert.DoesNotContain("orrent", text);
            Assert.DoesNotContain("peers", text);
            var job = message.Type == MessageTypes.JobsSnapshot ? shown.Payload!.Value.GetProperty("jobs")[0] : JobOf(shown);
            Assert.False(job.TryGetProperty("torrent", out _));
            Assert.False(job.TryGetProperty("statusNote", out _));
            var failure = job.GetProperty("failure");
            Assert.Equal(NativeBridgePolicy.AppManagedFailure, failure.GetProperty("message").GetString());
            // What a client acts on is kept.
            Assert.Equal(("unknown", false, true), (failure.GetProperty("kind").GetString(), failure.GetProperty("isRetryable").GetBoolean(),
                failure.GetProperty("isResumable").GetBoolean()));
            if (message.Type == MessageTypes.JobActionResult)
                Assert.Equal(NativeBridgePolicy.AppManagedError, shown.Payload!.Value.GetProperty("error").GetString());
            if (message.Type == MessageTypes.JobsSnapshot)
            {
                // An ordinary download's failure is its own.
                var plain = shown.Payload!.Value.GetProperty("jobs")[1];
                Assert.Equal("The server closed the connection.", plain.GetProperty("failure").GetProperty("message").GetString());
            }
        }
    }

    [Fact]
    public void AnOrdinaryDownloadsRefusalKeepsItsWords()
    {
        var message = IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = false, Error = "The file is in use.", Job = PlainJob() });
        Assert.Same(message, NativeBridgePolicy.To(BridgeRole.BrowserExtension, message));
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void EverySpellingOfTheDetailsGoesAndAStoppedOneThatWaitsKeepsRetry(BridgeRole role)
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["job"] = new Dictionary<string, object?>
            {
                ["url"] = FullMagnet, ["state"] = "cancelled", ["canResume"] = true, ["StatusNote"] = "x",
                ["torrent"] = new Dictionary<string, object?> { ["infoHash"] = Hash, ["awaitingFileChoice"] = true },
                ["Torrent"] = new Dictionary<string, object?> { ["infoHash"] = Hash },
            },
            ["Error"] = HostRefusal,
        });
        var message = new IpcMessage(Protocol.Version, MessageTypes.JobActionResult, "1", null, payload);

        var shown = NativeBridgePolicy.To(role, message)!;

        var job = JobOf(shown);
        Assert.DoesNotContain(job.EnumerateObject(), property =>
            property.Name.Equals("torrent", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("statusNote", StringComparison.OrdinalIgnoreCase));
        // Retry on a stopped download brings it back to its wait in the app, which the popup then shows; only a paused one
        // that waits is told it can't resume.
        Assert.True(job.GetProperty("canResume").GetBoolean());
        Assert.Equal(NativeBridgePolicy.AppManagedError, shown.Payload!.Value.GetProperty("Error").GetString());
    }
    [Theory]
    [MemberData(nameof(Roles))]
    public void A_finished_torrent_file_download_reaches_the_browser_as_an_ordinary_download(BridgeRole role)
    {
        // T10: the Host marks a finished .torrent download; the extension never hears of it (roadmap rule 5).
        var finished = PlainJob();
        finished.State = "completed";
        finished.TorrentFileReady = true;
        var plain = PlainJob();
        var messages = new[]
        {
            IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Jobs = [finished, plain] }),
            IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto { Job = finished, EventType = "completed" }),
            IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = true, Job = finished }),
        };

        foreach (var message in messages)
        {
            var shown = NativeBridgePolicy.To(role, message)!;
            var text = shown.Payload!.Value.GetRawText();
            Assert.DoesNotContain("orrent", text);
            var job = message.Type == MessageTypes.JobsSnapshot ? shown.Payload!.Value.GetProperty("jobs")[0] : JobOf(shown);
            Assert.False(job.TryGetProperty(NativeBridgePolicy.AppManagedProperty, out _));
            Assert.Equal("completed", job.GetProperty("state").GetString());
        }

        // Unmarked, the field isn't written at all, so an ordinary download's events pass as they are.
        var ordinary = IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto { Job = plain, EventType = "progress" });
        Assert.DoesNotContain("orrent", ordinary.Payload!.Value.GetRawText());
        Assert.Same(ordinary, NativeBridgePolicy.To(role, ordinary));
    }
}
