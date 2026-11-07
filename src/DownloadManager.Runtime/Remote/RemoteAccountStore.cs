using DownloadManager.Core.Providers;
using DownloadManager.Persistence;

namespace DownloadManager.Runtime.Remote;

public enum RemoteKeyState
{
    /// <summary>No key has been entered.</summary>
    None,
    Present,
    /// <summary>A key is saved and can't be read: a restored profile, another Windows user, a damaged file.</summary>
    Unreadable,
}

/// <summary>
/// One provider's account key, protected at rest. Nothing here hands the key back as a value to
/// display: outside this class it exists only as <see cref="KeySource"/>, for the provider's client.
/// A saved key that can't be read is its own state, so it never crashes the Host and is never
/// mistaken for no key having been set.
/// </summary>
public sealed class RemoteAccountStore : IRemoteSecretMatcher
{
    private readonly RemoteAccountFile _file;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Loaded _loaded = new(RemoteKeyState.None, null, new RemoteKeyPattern(null));

    private sealed record Loaded(RemoteKeyState State, string? Key, RemoteKeyPattern Pattern);

    private RemoteAccountStore(string providerId, RemoteAccountFile file, Action<string>? log)
    {
        ProviderId = providerId;
        _file = file;
        _log = log;
        KeySource = () => _loaded.Key;
    }

    public string ProviderId { get; }
    public RemoteKeyState State => _loaded.State;
    public bool HasKey => _loaded.State == RemoteKeyState.Present;

    /// <summary>Reads the current key each time, so a change takes effect on the next call.</summary>
    public Func<string?> KeySource { get; }

    /// <summary>The key changed. Handlers run after it was saved.</summary>
    public event EventHandler? Changed;

    public static async Task<RemoteAccountStore> OpenAsync(
        StoragePaths paths,
        string providerId,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var store = new RemoteAccountStore(providerId, new RemoteAccountFile(paths, providerId), log);
        await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _file.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (stored is null || string.IsNullOrEmpty(stored.ProtectedKey))
            {
                return;
            }

            var key = SecretProtector.UnprotectProviderKey(stored.ProtectedKey);
            if (string.IsNullOrWhiteSpace(key))
            {
                _log?.Invoke($"The saved {ProviderId} key can't be read on this Windows account.");
                _loaded = new Loaded(RemoteKeyState.Unreadable, null, new RemoteKeyPattern(null));
                return;
            }

            _loaded = new Loaded(RemoteKeyState.Present, key, new RemoteKeyPattern(key));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"The {ProviderId} account file can't be read: {ex.GetType().Name}.");
            _loaded = new Loaded(RemoteKeyState.Unreadable, null, new RemoteKeyPattern(null));
        }
    }

    /// <summary>Protects and saves the key, then starts using it. A failed save changes nothing.</summary>
    /// <exception cref="ArgumentException">Empty, or holds spaces or control characters.</exception>
    public async Task SetKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            // The message never quotes the value.
            throw new ArgumentException("An API key can't be empty or contain spaces.", nameof(key));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _file.SaveAsync(new StoredRemoteAccount
            {
                Provider = ProviderId,
                ProtectedKey = SecretProtector.ProtectProviderKey(trimmed),
                UpdatedAt = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
            _loaded = new Loaded(RemoteKeyState.Present, trimmed, new RemoteKeyPattern(trimmed));
        }
        finally { _gate.Release(); }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _file.Delete();
            _loaded = new Loaded(RemoteKeyState.None, null, new RemoteKeyPattern(null));
        }
        finally { _gate.Release(); }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Holds(string? text) => _loaded.Pattern.Holds(text);

    public string Mask(string text) => _loaded.Pattern.Mask(text);

    public override string ToString() => $"{ProviderId} account: {State}";
}
