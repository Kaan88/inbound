using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Android.Content;
using DownloadManager.Contracts;
using SystemClock = Android.OS.SystemClock;

namespace AndroidTrial;

public sealed record HostRequest(string RunId, string Phase, string? Url, string? Sha256, int Segments, bool ResumeIfPaused, bool Idle, int Count = 0);

/// <summary>
/// mode=host / mode=service: the Host core (<see cref="AndroidHost"/>) driven only through the dispatcher's envelopes.
/// phase=add: hello + snapshot, job.add, watch the events until the job ends, hash the file.
/// phase=resume: start again on the same state folder, report what Runtime's startup reconciliation did, then watch (and, only if
/// resumeIfPaused=1 and the job was left Paused, send job.resume as a client would).
/// idle: start the host, take the snapshot, do nothing else (the service keeps the process alive).
/// </summary>
internal static class HostScenario
{
    private static readonly object FileGate = new();

    public static string StateRoot(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "host-state");

    public static string ExtDir(Context context) => context.GetExternalFilesDir(null)!.AbsolutePath;

    public static async Task<AndroidHost> RunAsync(Context context, HostRequest request, Action<string> say, CancellationToken ct, bool keepHost)
    {
        StartupTrace.Mark("scenario.begin");
        var ext = ExtDir(context);
        var resultFile = Path.Combine(ext, $"host-result-{request.RunId}-{request.Phase}.json");
        var json = new JsonObject
        {
            ["runId"] = request.RunId,
            ["phase"] = request.Phase,
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        };
        long Since() => SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
        AndroidHost? host = null;
        try
        {
            json["hostStartBeginMs"] = Since();
            host = await AndroidHost.StartAsync(StateRoot(context), say, ct).ConfigureAwait(false);
            json["hostStartEndMs"] = Since();
            say($"host started {Since()} ms after process start");

            var session = new InProcessSession();
            var frames = new JsonArray();
            JobDto? watched = null;
            var ended = new TaskCompletionSource<JobDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSeen = new Dictionary<Guid, string>();
            var lastSay = DateTimeOffset.MinValue;
            session.OnFrame = message =>
            {
                try
                {
                    if (message.Type == MessageTypes.JobEvent)
                    {
                        var job = message.GetPayload<JobEventDto>().Job;
                        if (!firstSeen.TryGetValue(job.Id, out var prev) || prev != job.State)
                        {
                            firstSeen[job.Id] = job.State;
                            say($"event job {job.Id:N} -> {job.State} {job.ReceivedBytes}/{job.TotalBytes}");
                        }
                        else if (DateTimeOffset.UtcNow - lastSay > TimeSpan.FromSeconds(2))
                        {
                            lastSay = DateTimeOffset.UtcNow;
                            say($"progress {job.ReceivedBytes} / {job.TotalBytes} bytes state={job.State}");
                        }

                        if (watched is not null && job.Id == watched.Id && job.State is "completed" or "failed" or "cancelled")
                            ended.TrySetResult(job);
                    }
                    else frames.Add(message.Type);
                }
                catch (Exception ex) { say("frame error " + ex); }
            };

            // 1. hello + snapshot, through the dispatcher.
            var snapshotTcs = new TaskCompletionSource<SnapshotDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            HostHelloDto? hello = null;
            var inner = session.OnFrame;
            session.OnFrame = m =>
            {
                if (m.Type == MessageTypes.ServerHello) { StartupTrace.Mark("session.hello.frame"); hello = m.GetPayload<HostHelloDto>(); StartupTrace.Mark("session.hello.parsed"); }
                else if (m.Type == MessageTypes.JobsSnapshot) { StartupTrace.Mark("session.snapshot.frame"); var dto = m.GetPayload<SnapshotDto>(); StartupTrace.Mark("session.snapshot.parsed"); snapshotTcs.TrySetResult(dto); }
                inner(m);
            };
            StartupTrace.Mark("session.open.begin");
            await host.OpenAsync(session, ct).ConfigureAwait(false);
            StartupTrace.Mark("session.open.returned");
            var snapshot = await snapshotTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            json["hello"] = new JsonObject
            {
                ["hostVersion"] = hello?.HostVersion,
                ["protocol"] = hello?.ProtocolVersion,
                ["hostKind"] = hello?.HostKind,
                ["capabilities"] = new JsonArray((hello?.Capabilities ?? []).Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            };
            var jobsAtStart = new JsonArray();
            foreach (var j in snapshot.Jobs)
                jobsAtStart.Add(new JsonObject { ["id"] = j.Id.ToString(), ["state"] = j.State, ["received"] = j.ReceivedBytes, ["total"] = j.TotalBytes, ["part"] = j.PartFilePath, ["partExists"] = File.Exists(j.PartFilePath), ["partSize"] = File.Exists(j.PartFilePath) ? new FileInfo(j.PartFilePath).Length : -1 });
            json["snapshotJobsAtHello"] = jobsAtStart;
            json["snapshotStatus"] = new JsonObject { ["active"] = snapshot.Status.ActiveDownloads, ["queued"] = snapshot.Status.QueuedDownloads, ["total"] = snapshot.Status.TotalJobs, ["stateDir"] = snapshot.Status.StateDirectory };
            json["snapshotMs"] = Since();
            say($"hello ok, caps=[{string.Join(",", hello?.Capabilities ?? [])}], snapshot jobs={snapshot.Jobs.Count}; " + string.Join(" ", snapshot.Jobs.Select(j => $"{j.Id:N}:{j.State}:{j.ReceivedBytes}")));

            if (request.Phase == "seed")
            {
                json["seeded"] = await SeedAsync(context, host, session, request.Count, say, ct).ConfigureAwait(false);
            }
            else if (request.Idle)
            {
                json["idle"] = true;
            }
            else if (request.Phase == "add")
            {
                var dest = ExtDir(context);
                var add = IpcMessage.Create(MessageTypes.JobAdd, new AddJobRequest
                {
                    Url = request.Url ?? throw new ArgumentException("url"),
                    DestinationDirectory = dest,
                    FileName = $"host-{request.RunId}.bin",
                    Connections = request.Segments,
                    Source = "androidtrial",
                    ConflictPolicy = "overwrite",
                });
                var reply = await host.RequestAsync(session, add, ct).ConfigureAwait(false);
                var response = reply.GetPayload<AddJobResponse>();
                json["addReply"] = new JsonObject { ["type"] = reply.Type, ["accepted"] = response.Accepted, ["error"] = response.Error, ["errorCode"] = response.ErrorCode, ["state"] = response.Job?.State };
                say($"job.add -> {reply.Type} accepted={response.Accepted} error={response.Error} state={response.Job?.State}");
                if (response.Job is null) throw new InvalidOperationException("job.add returned no job: " + response.Error);
                watched = response.Job;
                json["jobId"] = watched.Id.ToString();
                json["finalPath"] = watched.FinalFilePath;
            }
            else
            {
                // What the Runtime did by itself at startup. Nothing here has sent a command.
                watched = snapshot.Jobs.FirstOrDefault(j => j.State is not ("completed" or "failed" or "cancelled"));
                if (watched is null) { json["note"] = "no unfinished job in the snapshot"; }
                else
                {
                    json["jobId"] = watched.Id.ToString();
                    json["finalPath"] = watched.FinalFilePath;
                    json["stateAtHello"] = watched.State;
                    json["receivedAtHello"] = watched.ReceivedBytes;
                    json["pausedByUserAtHello"] = watched.PausedByUser;
                    say($"after startup reconciliation: job {watched.Id:N} state={watched.State} received={watched.ReceivedBytes} (no command sent yet)");
                    await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                    var after = host.Service.GetJob(watched.Id);
                    json["stateAfter8sWithoutCommand"] = after?.State;
                    json["receivedAfter8sWithoutCommand"] = after?.ReceivedBytes;
                    say($"8 s later, no command: state={after?.State} received={after?.ReceivedBytes}");
                    if (after is not null && after.State is "completed" or "failed" or "cancelled") ended.TrySetResult(after);
                    else if (after?.State == "paused" && request.ResumeIfPaused)
                    {
                        var reply = await host.RequestAsync(session, IpcMessage.Create(MessageTypes.JobResume, new JobActionRequest { JobId = watched.Id }), ct).ConfigureAwait(false);
                        json["resumeCommandReply"] = reply.GetPayload<JobActionResponse>().Ok;
                        say("sent job.resume (job was left paused) -> " + reply.Type);
                    }
                }
            }

            if (watched is not null)
            {
                var done = await ended.Task.WaitAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                json["finalState"] = done.State;
                json["failure"] = done.Failure is null ? null : System.Text.Json.JsonSerializer.Serialize(done.Failure);
                json["receivedBytes"] = done.ReceivedBytes;
                json["totalBytes"] = done.TotalBytes;
                json["finalPath"] = done.FinalFilePath;
                json["finalExists"] = File.Exists(done.FinalFilePath);
                json["partGone"] = !File.Exists(done.PartFilePath);
                if (done.State == "completed" && File.Exists(done.FinalFilePath))
                {
                    await using var f = new FileStream(done.FinalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                    var sha = Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false)).ToLowerInvariant();
                    json["sha256"] = sha;
                    json["expectedSha256"] = request.Sha256;
                    json["hashMatched"] = request.Sha256 is null ? null : string.Equals(sha, request.Sha256, StringComparison.OrdinalIgnoreCase);
                }
            }

            json["otherFrameTypes"] = frames;
            json["trace"] = StartupTrace.ToJson();
            json["warm"] = WarmUp.ToJson();
            var cpu = CpuSnapshot.Take();
            json["processCpu"] = new JsonObject { ["user"] = Math.Round(cpu.UserS, 2), ["kernel"] = Math.Round(cpu.KernelS, 2), ["threads"] = new JsonArray(cpu.Threads.Select(t => (JsonNode?)new JsonObject { ["name"] = t.Value.Name, ["user"] = Math.Round(t.Value.User, 2), ["kernel"] = Math.Round(t.Value.Kernel, 2) }).Where(n => (double)n!["user"]! + (double)n["kernel"]! >= 0.02).ToArray()) };
            json["doneMs"] = Since();
            return host;
        }
        catch (Exception ex)
        {
            json["fatal"] = ex.ToString();
            json["trace"] = StartupTrace.ToJson();
            say("FATAL " + ex);
            return host!;
        }
        finally
        {
            lock (FileGate) File.WriteAllText(resultFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            say("RESULT " + json.ToJsonString());
            if (!keepHost && host is not null) await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// mode=host phase=seed: one job through the dispatcher (job.add, start mode "later", an unreachable address, so nothing is
    /// fetched), then its job file copied <paramref name="count"/> - 1 times with a new id and file name. The state this leaves is
    /// what a user with that many saved jobs has on disk. All jobs are unfinished ("later"), none completed.
    /// </summary>
    private static async Task<int> SeedAsync(Context context, AndroidHost host, InProcessSession session, int count, Action<string> say, CancellationToken ct)
    {
        var add = IpcMessage.Create(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = "http://127.0.0.1:9/seed.bin",
            DestinationDirectory = ExtDir(context),
            FileName = "seed-0.bin",
            StartMode = "later",
            Source = "androidtrial",
            ConflictPolicy = "overwrite",
        });
        var reply = await host.RequestAsync(session, add, ct).ConfigureAwait(false);
        var job = reply.GetPayload<AddJobResponse>().Job ?? throw new InvalidOperationException("seed job.add returned no job");
        var file = Path.Combine(host.Paths.JobsDirectory, job.Id + ".json");
        if (!File.Exists(file)) file = Path.Combine(host.Paths.JobsDirectory, job.Id.ToString("N") + ".json");
        var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        var made = 1;
        for (var i = 1; i < count; i++)
        {
            var id = Guid.NewGuid();
            var copy = text.Replace(job.Id.ToString(), id.ToString()).Replace(job.Id.ToString("N"), id.ToString("N")).Replace("seed-0.bin", $"seed-{i}.bin");
            await File.WriteAllTextAsync(Path.Combine(host.Paths.JobsDirectory, Path.GetFileName(file).Replace(job.Id.ToString("N"), id.ToString("N")).Replace(job.Id.ToString(), id.ToString())), copy, ct).ConfigureAwait(false);
            made++;
        }

        say($"seeded {made} job files ({text.Length} chars each) in {host.Paths.JobsDirectory}");
        return made;
    }
}
