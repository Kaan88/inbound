using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

/// <summary>One provider with what it can do. An ability it doesn't have is simply null.</summary>
public sealed class RemoteProviderEntry(IRemoteProvider provider, RemoteAccountStore? account)
{
    public IRemoteProvider Provider { get; } = provider;
    public RemoteAccountStore? Account { get; } = account;

    public string Id => Provider.Id;
    public string DisplayName => Provider.DisplayName;
    public RemoteCapabilities Capabilities => Provider.Capabilities;
    public RemoteTransferPolicy TransferPolicy => Provider.TransferPolicy;

    public IRemoteCatalog? Catalog => Provider as IRemoteCatalog;
    public IRemoteCacheChecker? CacheChecker => Provider as IRemoteCacheChecker;
    public IRemoteSubmitter? Submitter => Provider as IRemoteSubmitter;
    public IRemoteLinkResolver? LinkResolver => Provider as IRemoteLinkResolver;
    public IRemoteHosterDirectory? Hosters => Provider as IRemoteHosterDirectory;
    public IRemoteItemRemover? Remover => Provider as IRemoteItemRemover;
    public IRemoteQueue? Queue => Provider as IRemoteQueue;
    public IRemoteFolderLinkParser? FolderLinks => Provider as IRemoteFolderLinkParser;

    /// <summary>Why this provider can't be called at all right now, in words for the user. Null when it can.</summary>
    public string? AccountProblem() => Account?.State switch
    {
        RemoteKeyState.None => $"No {DisplayName} API key is set. Add it in Options, then try again.",
        RemoteKeyState.Unreadable => $"The saved {DisplayName} API key can't be read on this Windows account. Enter it again in Options, then try again.",
        _ => null,
    };
}

/// <summary>
/// Provider id to provider. Everything above this looks a provider up by the id a job or remote
/// item carries, so only the Host's composition ever names a concrete one. Filled once at startup.
/// </summary>
public sealed class RemoteProviderRegistry : IRemoteSecretMatcher
{
    private readonly Dictionary<string, RemoteProviderEntry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyCollection<RemoteProviderEntry> All => _entries.Values;

    public RemoteProviderEntry Add(IRemoteProvider provider, RemoteAccountStore? account = null)
    {
        var entry = new RemoteProviderEntry(provider, account);
        _entries.Add(provider.Id, entry);
        return entry;
    }

    public RemoteProviderEntry? Find(string? providerId)
        => providerId is not null && _entries.TryGetValue(providerId, out var entry) ? entry : null;

    public bool Holds(string? text) => _entries.Values.Any(entry => entry.Account?.Holds(text) == true);

    public string Mask(string text)
        => _entries.Values.Aggregate(text, (current, entry) => entry.Account?.Mask(current) ?? current);
}
