using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DownloadManager.Core;

namespace DownloadManager.Torrents;

/// <summary>
/// T3: a torrent's own options (limits, download order, seeding goal), a file check, an announce, the Overview's facts,
/// and the session-wide peer settings and progress rate. A worker older than a command answers "unknown command"; the
/// engine then says <see cref="TorrentActionOutcome.OlderWorker"/> and does not ask again.
/// </summary>
public sealed partial class TorrentDownloadEngine
{
    /// <summary>Progress interval while someone watches torrents, and while nobody does.</summary>
    public const int WatchedProgressMs = 1000;
    public const int IdleProgressMs = 5000;

    private volatile bool _workerLacksAnnounce;
    private volatile bool _workerLacksSequential;
    private volatile bool _workerLacksDetails;

    /// <summary>A torrent's own seeding limits (its goal is a ratio or hours); the others follow <see cref="UpdateSeedPolicy"/>.</summary>
    private readonly ConcurrentDictionary<Guid, TorrentSeedPolicy> _seedPolicies = new();

    /// <summary>The seeding limits of <paramref name="jobId"/>: its own, or Options'.</summary>
    private TorrentSeedPolicy SeedPolicyOf(Guid jobId) => _seedPolicies.TryGetValue(jobId, out var own) ? own : _seedPolicy;

    /// <summary>Keeps (or drops) the job's own seeding limits.</summary>
    private void NoteSeedGoal(Guid jobId, TorrentJobInfo info)
    {
        if (info.SeedGoal is TorrentSeedGoals.Ratio or TorrentSeedGoals.Hours)
            _seedPolicies[jobId] = TorrentSeedGoals.PolicyFor(info, _seedPolicy);
        else
            _seedPolicies.TryRemove(jobId, out _);
    }

    /// <summary>The add fields for a torrent's own limits and download order (0 = none).</summary>
    internal static void AddOptions(JsonObject arguments, TorrentJobInfo info)
    {
        arguments["downloadLimit"] = LimitValue(info.DownloadLimit);
        arguments["uploadLimit"] = LimitValue(info.UploadLimit);
        if (info.Sequential) arguments["sequential"] = true;
    }

    private static long LimitValue(long? limit) => limit is > 0 ? Math.Min(limit.Value, int.MaxValue) : 0;

    public Task<TorrentActionReply> RecheckAsync(Guid jobId, CancellationToken cancellationToken)
        => ActAsync(jobId, "recheck", null, () => false, () => { }, cancellationToken);

    public Task<TorrentActionReply> AnnounceAsync(Guid jobId, CancellationToken cancellationToken)
        => ActAsync(jobId, "announce", null, () => _workerLacksAnnounce, () => _workerLacksAnnounce = true, cancellationToken);

    public async Task<TorrentActionReply> ApplyOptionsAsync(Guid jobId, TorrentJobInfo info, CancellationToken cancellationToken)
    {
        NoteSeedGoal(jobId, info);
        // Every seeding torrent checks its limits again (the one whose goal changed among them).
        var previous = Interlocked.Exchange(ref _seedPolicyChanged, new CancellationTokenSource());
        previous.Cancel();
        if (AttachedSession(jobId) is null) return TorrentActionReply.NotRunning;
        var limits = await ActAsync(jobId, "setTorrentLimits",
            new JsonObject { ["download"] = LimitValue(info.DownloadLimit), ["upload"] = LimitValue(info.UploadLimit) },
            () => false, () => { }, cancellationToken).ConfigureAwait(false);
        if (limits.Outcome != TorrentActionOutcome.Applied) return limits;
        return await ActAsync(jobId, "sequential", new JsonObject { ["on"] = info.Sequential },
            () => _workerLacksSequential, () => _workerLacksSequential = true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TorrentDetails?> GetDetailsAsync(Guid jobId, CancellationToken cancellationToken)
        => await QueryAsync(jobId, "details", () => _workerLacksDetails, () => _workerLacksDetails = true, cancellationToken).ConfigureAwait(false) is { } reply
            ? ReadDetails(reply) : null;

    public async Task<long?> GetDhtNodesAsync(CancellationToken cancellationToken)
        => await _supervisor.StatsAsync(cancellationToken).ConfigureAwait(false) is { } reply &&
           reply["dhtNodes"] is JsonValue value && value.TryGetValue<long>(out var nodes)
            ? nodes
            : null;

    public void UpdatePeerSettings(TorrentPeerSettings settings) => _ = _supervisor.SetPeerSettingsAsync(settings);

    public void SetProgressWatched(bool watched) => _supervisor.SetProgressInterval(watched ? WatchedProgressMs : IdleProgressMs);

    /// <summary>Sends a command for one attached torrent and says what came of it.</summary>
    private async Task<TorrentActionReply> ActAsync(Guid jobId, string command, JsonObject? arguments, Func<bool> lacking, Action lacks,
        CancellationToken cancellationToken)
    {
        if (AttachedSession(jobId) is not { } session) return TorrentActionReply.NotRunning;
        if (lacking()) return TorrentActionReply.OlderWorker;
        try
        {
            await session.SendAsync(command, arguments, cancellationToken).ConfigureAwait(false);
            return TorrentActionReply.Applied;
        }
        catch (TorrentCommandException e) when (e.Reason == "unknown command")
        {
            lacks();
            _log?.Invoke($"The torrent worker can't do {command} (an older worker).");
            return TorrentActionReply.OlderWorker;
        }
        catch (TorrentCommandException e) when (e.Reason == "unknown torrent")
        {
            return TorrentActionReply.NotRunning;
        }
        catch (TorrentCommandException e)
        {
            return new TorrentActionReply(TorrentActionOutcome.Refused, e.Reason);
        }
        catch (TorrentEngineExitedException)
        {
            return TorrentActionReply.NotRunning;
        }
    }

    internal static TorrentDetails ReadDetails(JsonObject reply)
        => new(Text(reply, "infoHashV1"), Text(reply, "infoHashV2"), Bool(reply, "private"), Long(reply, "wasted"),
            Long(reply, "failedBytes"), Long(reply, "hashFailures"), Int(reply, "pieces"), Long(reply, "pieceLength"),
            Int(reply, "connections"), Int(reply, "connectionsLimit"), Long(reply, "downloadLimit"), Long(reply, "uploadLimit"),
            Bool(reply, "sequential"), Bool(reply, "pex"), Double(reply, "distributedCopies"), Long(reply, "nextAnnounce"),
            Long(reply, "addedAt"), Long(reply, "activeSeconds"), reply["dhtNodes"] is null ? -1 : Long(reply, "dhtNodes"));

    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
