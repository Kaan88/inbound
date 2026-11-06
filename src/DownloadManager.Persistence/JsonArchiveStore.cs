using System.Text.Json;
using DownloadManager.Core.Archives;

namespace DownloadManager.Persistence;

/// <summary>
/// <c>archive/settings.json</c>, <c>archive/sets/&lt;id&gt;.json</c> and
/// <c>archive/packages/&lt;id&gt;.json</c>, each written atomically. An unreadable document is moved
/// aside, as a job's is, and treated as absent.
/// </summary>
public sealed class JsonArchiveStore(StoragePaths paths, Action<string>? log = null) : IArchiveStore
{
    private readonly string _settingsFile = Path.Combine(paths.Root, "archive", "settings.json");
    private readonly string _setsDirectory = Path.Combine(paths.Root, "archive", "sets");
    private readonly string _packagesDirectory = Path.Combine(paths.Root, "archive", "packages");

    public async Task<ArchiveSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFile)) return null;
        var settings = await ReadAsync<ArchiveSettings>(_settingsFile, cancellationToken).ConfigureAwait(false);
        if (settings is not null && settings.SchemaVersion > ArchiveSettings.CurrentSchemaVersion)
        {
            Quarantine(_settingsFile, $"schema {settings.SchemaVersion} is newer than this version");
            return null;
        }

        return settings;
    }

    public Task SaveSettingsAsync(ArchiveSettings settings, CancellationToken cancellationToken = default)
        => WriteAsync(_settingsFile, settings, cancellationToken);

    public async Task<IReadOnlyList<ArchiveSetRecord>> LoadSetsAsync(CancellationToken cancellationToken = default)
    {
        var sets = new List<ArchiveSetRecord>();
        if (!Directory.Exists(_setsDirectory)) return sets;
        foreach (var file in Directory.EnumerateFiles(_setsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var set = await ReadAsync<ArchiveSetRecord>(file, cancellationToken).ConfigureAwait(false);
            if (set is null) continue;
            if (set.SchemaVersion > ArchiveSetRecord.CurrentSchemaVersion || set.Id == Guid.Empty || string.IsNullOrWhiteSpace(set.Key))
            {
                Quarantine(file, "the document is not one this version can use");
                continue;
            }

            sets.Add(set);
        }

        return sets;
    }

    public Task SaveSetAsync(ArchiveSetRecord set, CancellationToken cancellationToken = default)
        => WriteAsync(Path.Combine(_setsDirectory, $"{set.Id:N}.json"), set, cancellationToken);

    public Task DeleteSetAsync(Guid id, CancellationToken cancellationToken = default)
        => DeleteAsync(Path.Combine(_setsDirectory, $"{id:N}.json"));

    public async Task<IReadOnlyList<ArchivePackagePassword>> LoadPackagePasswordsAsync(CancellationToken cancellationToken = default)
    {
        var passwords = new List<ArchivePackagePassword>();
        if (!Directory.Exists(_packagesDirectory)) return passwords;
        foreach (var file in Directory.EnumerateFiles(_packagesDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReadAsync<ArchivePackagePassword>(file, cancellationToken).ConfigureAwait(false) is { } password)
            {
                passwords.Add(password);
            }
        }

        return passwords;
    }

    public Task SavePackagePasswordAsync(ArchivePackagePassword password, CancellationToken cancellationToken = default)
        => WriteAsync(Path.Combine(_packagesDirectory, $"{password.PackageId:N}.json"), password, cancellationToken);

    public Task DeletePackagePasswordAsync(Guid packageId, CancellationToken cancellationToken = default)
        => DeleteAsync(Path.Combine(_packagesDirectory, $"{packageId:N}.json"));

    private static Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
        => AtomicFile.WriteAsync(path, JsonSerializer.Serialize(value, PersistenceJson.Options), cancellationToken);

    private static Task DeleteAsync(string path)
    {
        File.Delete(path);
        return Task.CompletedTask;
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json, PersistenceJson.Options) ?? throw new InvalidDataException("The document was empty.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            Quarantine(path, exception.GetType().Name);
            return null;
        }
    }

    private void Quarantine(string path, string reason)
    {
        log?.Invoke($"Set aside archive document '{Path.GetFileName(path)}': {reason}.");
        try
        {
            paths.EnsureCreated();
            File.Move(path, Path.Combine(paths.CorruptDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-archive-{Path.GetFileName(path)}"), overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not set aside '{Path.GetFileName(path)}': {exception.GetType().Name}.");
        }
    }
}
