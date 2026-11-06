using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

/// <summary>What became of a copied link: words for the tray, and something for the log. Neither holds the address's secrets.</summary>
public sealed record CopiedLinkOutcome(string? Title = null, string? Words = null, string? Problem = null);

/// <summary>
/// What copying a link does. With no usable provider key that is a direct add, and nothing else happens.
/// With one, a hoster link never gets a direct request, so it waits on the list for the user's go-ahead:
/// copying something must not spend the account's quota by itself. A link nobody can classify isn't added.
/// </summary>
public sealed class CopiedLinks(DownloadManagerService service, RemoteJobCoordinator jobs)
{
    private const string NotAddedTitle = "Copied link not added";
    private static readonly TimeSpan LongestCheck = TimeSpan.FromSeconds(30);

    public Task<CopiedLinkOutcome> AddAsync(string url)
        => jobs.HasUsableAccount
            // Off the clipboard's thread, which is the tray's: a provider's patterns alone can take seconds.
            ? Task.Run(() => RouteAsync(url))
            : AddDirectlyAsync(url);

    /// <summary>A copied address is someone's private input, and with an account it may be a hoster link. The site says enough then.</summary>
    public string DescribeForLog(string url)
        => jobs.HasUsableAccount && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Scheme) + uri.IdnHost
            : UrlRedactor.Redact(url);

    private async Task<CopiedLinkOutcome> RouteAsync(string url)
    {
        RemoteInputClassification classified;
        try
        {
            using var limit = new CancellationTokenSource(LongestCheck);
            classified = await jobs.ClassifyAsync(url, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CouldNotCheck(jobs.UsableProviderName, $"it could not be checked ({ex.GetType().Name})");
        }

        switch (classified.Kind)
        {
            case RemoteInputKind.OrdinaryLink or RemoteInputKind.ProviderDirectLink:
                return await AddDirectlyAsync(url).ConfigureAwait(false);

            case RemoteInputKind.HosterLink or RemoteInputKind.FolderLink when jobs.IsRunning:
                return await HoldAsync(url, classified).ConfigureAwait(false);

            case RemoteInputKind.Undetermined or RemoteInputKind.HosterLink or RemoteInputKind.FolderLink:
                return CouldNotCheck(classified.ProviderName ?? jobs.UsableProviderName, classified.Kind.ToString());

            default:
                return new CopiedLinkOutcome(NotAddedTitle, "This link can't be added from the clipboard. Use Add Links to see why.",
                    $"Clipboard link not added: {classified.Kind}.");
        }
    }

    private async Task<CopiedLinkOutcome> HoldAsync(string url, RemoteInputClassification classified)
    {
        RemoteAddResult added;
        try
        {
            added = await jobs.AddAsync(new RemoteAddRequest { Input = url, HoldForApproval = true }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CouldNotCheck(classified.ProviderName ?? jobs.UsableProviderName, $"it could not be saved ({ex.GetType().Name})");
        }

        if (added.Job is null)
        {
            return CouldNotCheck(classified.ProviderName ?? jobs.UsableProviderName, added.Kind?.ToString() ?? "refused");
        }

        // Already on the list, in whatever state it has reached. Copying it again changes nothing.
        if (added.Existing)
        {
            return new CopiedLinkOutcome();
        }

        var provider = added.Job.ProviderName;
        return new CopiedLinkOutcome("Copied link is waiting", classified.Kind == RemoteInputKind.FolderLink
            ? $"This link is to an item on your {provider} account. It is on the list, waiting for your approval."
            : $"{provider} handles this link. It is on the list, waiting for your approval. Nothing has been sent.");
    }

    private static CopiedLinkOutcome CouldNotCheck(string provider, string why)
        => new(NotAddedTitle, $"It couldn't be settled whether {provider} handles this link, so nothing was added. Use Add Links to add it.",
            $"Clipboard link not added: {why}.");

    /// <summary>What copying a link has always done. The add itself failing is the caller's to report.</summary>
    private async Task<CopiedLinkOutcome> AddDirectlyAsync(string url)
    {
        var result = await service.AddJobAsync(new AddJobRequest
        {
            Url = url,
            Source = "clipboardMonitor",
            StartMode = "now",
        }).ConfigureAwait(false);

        if (result.Accepted && !result.Duplicate)
        {
            return new CopiedLinkOutcome("Clipboard download added", result.Job?.FileName ?? UrlRedactor.Redact(url));
        }

        return result.Accepted
            ? new CopiedLinkOutcome()
            : new CopiedLinkOutcome(Problem: $"Clipboard URL rejected ({UrlRedactor.Redact(url)}): {result.Error}");
    }
}
