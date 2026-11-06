using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

/// <summary>What keeps a hoster link from getting a direct request by any way other than adding one.</summary>
public sealed partial class RemoteJobCoordinator : IDirectAddressPolicy
{
    /// <summary>At least one account could take something now. Answers from memory, with no file or network involved.</summary>
    public bool HasUsableAccount => _accounts.Values.Any(work => work.Entry.AccountProblem() is null);

    /// <summary>False when it never started, so a job added now would be seen by nobody.</summary>
    public bool IsRunning => _started;

    /// <summary>For words about an account when nothing more specific is known.</summary>
    public string UsableProviderName
        => _accounts.Values.FirstOrDefault(work => work.Entry.AccountProblem() is null)?.Entry.DisplayName ?? "your provider";

    public async Task<string?> WhyNotDirectAsync(string address, bool explicitDirect, CancellationToken cancellationToken = default)
    {
        if (!HasUsableAccount ||
            !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        RemoteInputClassification classified;
        try
        {
            classified = await ClassifyAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log?.Invoke($"An address could not be checked: {ex.GetType().Name}.");
            return explicitDirect ? null : $"It couldn't be checked whether {UsableProviderName} handles this link, so the address wasn't changed. Try again, or ask for a direct request.";
        }

        var provider = classified.ProviderName ?? UsableProviderName;
        switch (classified.Kind)
        {
            case RemoteInputKind.HosterLink:
                return $"{provider} handles this site, so it can't be a download's direct address. Add it with Add Links.";
            case RemoteInputKind.FolderLink:
                return $"This link is to an item on your {provider} account, so it can't be a download's direct address. Add it with Add Links.";
            case RemoteInputKind.Undetermined when !explicitDirect:
                return $"{UrlRedactor.HideAddresses(classified.Reason)} To use this address anyway, ask for a direct request.";
            case RemoteInputKind.OrdinaryLink when classified.CheckIncomplete && !explicitDirect:
                return $"{UrlRedactor.HideAddresses(classified.Note)} To use this address anyway, ask for a direct request.";
            case RemoteInputKind.Unsupported:
                return UrlRedactor.HideAddresses(classified.Reason);
            default:
                return null;
        }
    }
}
