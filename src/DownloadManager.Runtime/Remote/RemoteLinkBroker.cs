using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

public sealed record RemoteLinkBrokerOptions
{
    /// <summary>Tries of one provider call within a run, counting the first.</summary>
    public int MaxAttempts { get; init; } = 4;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The longest a job waits for a provider's Retry-After before giving up for this run.</summary>
    public TimeSpan LongestRetryAfter { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Gets a job its address for one run. Before each link it asks the provider whether the item is
/// still there and whether the file is still the one the job's bytes belong to, because a link
/// alone proves neither. Temporary failures are waited out here, under the run's cancellation; the
/// rest become the job's failure in plain words, keeping what was already downloaded. Never touches
/// the remote item, and never looks at another job's files.
/// </summary>
public sealed class RemoteLinkBroker : IRemoteLinkBroker
{
    private readonly RemoteProviderRegistry _providers;
    private readonly RemoteLinkBrokerOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    public RemoteLinkBroker(
        RemoteProviderRegistry providers,
        RemoteLinkBrokerOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _providers = providers;
        _options = options ?? new RemoteLinkBrokerOptions();
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    public async Task<RemoteLinkResolution> PrepareRunAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        if (job.RemoteSource is not { } source)
        {
            return Fail(FailureKind.Unknown, "This download doesn't come from a provider.", retryable: false);
        }

        var entry = _providers.Find(source.Provider);
        if (entry?.Catalog is not { } catalog || entry.LinkResolver is not { } resolver)
        {
            return Fail(FailureKind.Unknown, $"This download comes from {source.Provider}, and that account is not available in this app.", retryable: false);
        }

        if (!RemoteKinds.TryParse(source.Kind, out var kind))
        {
            return Fail(FailureKind.Unknown, $"This download's {entry.DisplayName} reference can't be read.", retryable: false);
        }

        if (entry.AccountProblem() is { } problem)
        {
            return Fail(FailureKind.Unauthorized, problem, retryable: false);
        }

        var reference = new RemoteItemRef(source.Provider, kind, source.ItemId);
        try
        {
            var item = await CallAsync(() => catalog.GetAsync(reference, fresh: true, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return ItemGone(entry);
            }

            if (ItemProblem(entry, item) is { } itemProblem)
            {
                return itemProblem;
            }

            var file = item.Files.FirstOrDefault(candidate => candidate.FileId == source.FileId);
            if (file is null)
            {
                return Fail(FailureKind.NotFound,
                    $"{entry.DisplayName} no longer lists this file in its item. Anything already downloaded was kept.", retryable: false);
            }

            if (file.Infected)
            {
                return Fail(FailureKind.Forbidden, $"{entry.DisplayName} marks this file as infected, so it won't be downloaded.", retryable: false);
            }

            var link = await CallAsync(() => resolver.ResolveLinkAsync(reference, source.FileId, cancellationToken), cancellationToken).ConfigureAwait(false);
            var changed = IdentityChanged(job, file);
            if (changed)
            {
                _log?.Invoke($"Job {job.Id}: the file changed on {entry.DisplayName}, so its local data is being discarded.");
            }

            var size = file.Size > 0 ? file.Size : (long?)null;
            return RemoteLinkResolution.Link(
                link.Url.AbsoluteUri,
                resetLocalData: changed,
                expectedTotalBytes: size,
                expectedMd5: string.IsNullOrWhiteSpace(file.Md5) ? null : file.Md5,
                maxConnections: entry.TransferPolicy.MaxConnectionsPerFile);
        }
        catch (RemoteProviderException ex)
        {
            return FromProvider(entry, ex);
        }
    }

    /// <summary>
    /// The CDN's answer says nothing about why a link stopped working, so any of these earns a new
    /// one, and the provider's own view of the item then decides what really happened. A proxy's
    /// refusal (407) is about the route, and a new link would meet it too.
    /// </summary>
    public Task<bool> ShouldRefreshAfterFailureAsync(DownloadJob job, JobFailure failure, CancellationToken cancellationToken)
    {
        var deadLink = failure.Kind is FailureKind.ExpiredUrl or FailureKind.Unauthorized or FailureKind.Forbidden or FailureKind.NotFound;
        return Task.FromResult(deadLink && failure.HttpStatus != 407 && _providers.Find(job.RemoteSource?.Provider) is not null);
    }

    private static RemoteLinkResolution? ItemProblem(RemoteProviderEntry entry, RemoteItem item) => item.Stage switch
    {
        RemoteStage.Ready => null,
        RemoteStage.Failed => Fail(FailureKind.Unknown,
            $"{entry.DisplayName} reports that this item failed: {Said(entry, item.Error ?? item.RawState ?? "no reason given")}.", retryable: false),
        _ => Fail(FailureKind.Unknown,
            $"{entry.DisplayName} isn't offering this item right now (state: {Said(entry, item.RawState ?? "unknown", 100)}). Try again later.", retryable: true),
    };

    /// <summary>
    /// A provider that reports no size or no md5 has said nothing against the file, and an md5 the
    /// job didn't have before can only be checked at the end. The engine still compares Last-Modified
    /// against the checkpoint on every resume, which is what catches same-size changes here.
    /// </summary>
    private static bool IdentityChanged(DownloadJob job, RemoteFile file)
    {
        if (job.ExpectedTotalBytes is > 0 && file.Size > 0 && job.ExpectedTotalBytes != file.Size)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(job.ExpectedMd5) && !string.IsNullOrWhiteSpace(file.Md5) &&
               !job.ExpectedMd5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (RemoteProviderException ex) when (ex.Kind is RemoteFailureKind.RateLimited or RemoteFailureKind.ServiceError or RemoteFailureKind.Network or RemoteFailureKind.BadResponse)
            {
                var wait = BackoffFor(attempt, ex);
                if (attempt >= _options.MaxAttempts || wait is null)
                {
                    throw;
                }

                await Task.Delay(wait.Value, _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Null when the provider asks for a longer wait than a run should sit on a download slot.</summary>
    private TimeSpan? BackoffFor(int attempt, RemoteProviderException failure)
    {
        var doubled = _options.InitialDelay * Math.Pow(2, attempt - 1);
        var delay = (doubled > _options.MaxDelay ? _options.MaxDelay : doubled) * (0.8 + (Random.Shared.NextDouble() * 0.4));
        if (failure.RetryAfter is not { } asked)
        {
            return delay;
        }

        return asked > _options.LongestRetryAfter ? null : asked > delay ? asked : delay;
    }

    private static RemoteLinkResolution FromProvider(RemoteProviderEntry entry, RemoteProviderException failure)
    {
        var name = entry.DisplayName;
        var said = Said(entry, failure.Message);
        return failure.Kind switch
        {
            RemoteFailureKind.Authentication =>
                Fail(FailureKind.Unauthorized, $"{name} refused the API key. Check it in Options, then try again. ({said})", retryable: false),
            RemoteFailureKind.AccountLimit =>
                Fail(FailureKind.Forbidden, $"Your {name} account doesn't allow this: {said}", retryable: false),
            RemoteFailureKind.AccountBusy =>
                Fail(FailureKind.Http, $"Your {name} account is at its limit for now: {said}", retryable: true),
            RemoteFailureKind.RateLimited =>
                Fail(FailureKind.Http, $"{name} is asking for fewer requests. Try again in a while. ({said})", retryable: true),
            RemoteFailureKind.NotFound => ItemGone(entry),
            RemoteFailureKind.Rejected or RemoteFailureKind.LinkPasswordRequired or RemoteFailureKind.NotCached or RemoteFailureKind.AlreadyExists =>
                Fail(FailureKind.Unknown, $"{name} refused to supply a link: {said}", retryable: false),
            RemoteFailureKind.ServiceError =>
                Fail(FailureKind.Http, $"{name} is having problems: {said}", retryable: true),
            RemoteFailureKind.Network =>
                Fail(FailureKind.Network, $"{name} could not be reached. Check your connection and try again.", retryable: true),
            _ =>
                Fail(FailureKind.Unknown, $"{name} sent an answer this app can't read. Try again later.", retryable: true),
        };
    }

    private static RemoteLinkResolution ItemGone(RemoteProviderEntry entry) => Fail(FailureKind.NotFound,
        $"This item is no longer on your {entry.DisplayName} account. Anything already downloaded was kept.", retryable: false);

    /// <summary>
    /// A provider's words can be anything, of any length, and these end up saved in job JSON and sent to
    /// every client with each snapshot. A thousand long ones would be more than a snapshot can carry.
    /// </summary>
    private static string Said(RemoteProviderEntry entry, string words, int longest = 500)
        => RemoteText.Clean(words, longest, entry.Account is { } account ? account.Mask : null) ?? "no reason given";

    /// <summary>Everything a failure here leaves in place can be picked up again, so all of them can be resumed.</summary>
    private static RemoteLinkResolution Fail(FailureKind kind, string message, bool retryable)
        => RemoteLinkResolution.Fail(new JobFailure(kind, message, retryable, IsResumable: true, DateTimeOffset.UtcNow));
}
