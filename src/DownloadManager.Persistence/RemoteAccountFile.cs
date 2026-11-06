using System.Text.Json;

namespace DownloadManager.Persistence;

/// <summary>What a provider's account file holds. The key is only ever ciphertext here.</summary>
public sealed record StoredRemoteAccount
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Provider { get; init; } = string.Empty;
    public string? ProtectedKey { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// One provider's <c>remote/account-&lt;provider&gt;.json</c>, written like every other durable
/// file. Provider state stays out of settings.json, because saving Options drops fields it
/// doesn't know and a damaged settings file resets to defaults.
/// </summary>
public sealed class RemoteAccountFile
{
    private readonly string _path;

    public RemoteAccountFile(StoragePaths paths, string providerId)
    {
        if (string.IsNullOrEmpty(providerId) || !providerId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("Not a provider id.", nameof(providerId));
        }

        _path = Path.Combine(paths.RemoteDirectory, $"account-{providerId}.json");
    }

    /// <returns>Null when there is no file.</returns>
    /// <exception cref="InvalidDataException">The file exists and can't be understood.</exception>
    public async Task<StoredRemoteAccount?> LoadAsync(CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredRemoteAccount>(json, PersistenceJson.Options)
                ?? throw new InvalidDataException("The account file is empty.");
            if (stored.SchemaVersion > StoredRemoteAccount.CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Unsupported account schema {stored.SchemaVersion}.");
            }

            return stored;
        }
        catch (JsonException)
        {
            // Not passed on: a JsonException's text can quote the file.
            throw new InvalidDataException("The account file is damaged.");
        }
    }

    public Task SaveAsync(StoredRemoteAccount account, CancellationToken cancellationToken = default)
        => AtomicFile.WriteAsync(_path, JsonSerializer.Serialize(account, PersistenceJson.Options), cancellationToken);

    public void Delete() => File.Delete(_path);
}
