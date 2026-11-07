using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

/// <summary>
/// A provider's list of sites, kept for an hour. A failed refresh keeps using the list it has, and
/// isn't tried again for a minute. Availability shown from here is at most that old, and the
/// provider has the last word when a link is submitted.
/// </summary>
public sealed class RemoteHosterCache(IRemoteHosterDirectory directory, TimeProvider time, Action<string>? log = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailedRefreshPause = TimeSpan.FromMinutes(1);
    private const int MostHosters = 5000;

    private readonly Lock _sync = new();
    private IReadOnlyList<RemoteHosterMatcher>? _hosters;
    private DateTimeOffset _freshUntil;
    private DateTimeOffset? _fetchedAt;
    private DateTimeOffset _retryAfter;
    private string? _problem;
    private Task? _refresh;

    /// <summary>Null problem means the list is current enough. Network calls happen outside any lock.</summary>
    public async Task<(IReadOnlyList<RemoteHosterMatcher> Hosters, string? Problem)> GetAsync(CancellationToken cancellationToken = default)
    {
        Task refresh;
        lock (_sync)
        {
            var now = time.GetUtcNow();
            if (_hosters is not null && now < _freshUntil)
            {
                return (_hosters, null);
            }

            if (now < _retryAfter)
            {
                return (_hosters ?? [], _problem);
            }

            // On another thread, so it can't finish and clear this field before the assignment.
            refresh = _refresh ??= Task.Run(RefreshAsync);
        }

        await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync) return (_hosters ?? [], _problem);
    }

    /// <summary>When the list in hand was fetched. Null before the first fetch worked.</summary>
    public DateTimeOffset? FetchedAt { get { lock (_sync) return _fetchedAt; } }

    /// <summary>Whether the provider currently offers this hoster. Null when the list can't say.</summary>
    public async Task<RemoteHoster?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        var (hosters, _) = await GetAsync(cancellationToken).ConfigureAwait(false);
        return hosters.FirstOrDefault(matcher => matcher.Hoster.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Hoster;
    }

    /// <summary>Another key can mean another account's limits.</summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _freshUntil = DateTimeOffset.MinValue;
            _retryAfter = DateTimeOffset.MinValue;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var listed = await directory.GetHostersAsync(CancellationToken.None).ConfigureAwait(false);
            var matchers = new List<RemoteHosterMatcher>();
            var skipped = 0;
            foreach (var hoster in listed.Take(MostHosters))
            {
                if (RemoteHosterMatcher.Create(hoster) is { } matcher)
                {
                    matchers.Add(matcher);
                }
                else
                {
                    skipped++;
                }
            }

            if (skipped > 0)
            {
                log?.Invoke($"Skipped {skipped} hoster pattern(s) that could not be used.");
            }

            lock (_sync)
            {
                _hosters = matchers;
                _fetchedAt = time.GetUtcNow();
                _freshUntil = _fetchedAt.Value + Lifetime;
                _retryAfter = DateTimeOffset.MinValue;
                _problem = null;
            }
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the list is an aid to sorting links and not worth failing anything over.
            log?.Invoke($"Could not fetch the hoster list: {ex.GetType().Name}.");
            lock (_sync)
            {
                _retryAfter = time.GetUtcNow() + FailedRefreshPause;
                _problem = "The list of supported sites couldn't be fetched, so this link may be one the provider can handle.";
            }
        }
        finally
        {
            lock (_sync) _refresh = null;
        }
    }
}
