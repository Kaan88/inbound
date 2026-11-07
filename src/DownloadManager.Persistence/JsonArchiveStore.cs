using System.Text.Json;
using DownloadManager.Core.Archives;

namespace DownloadManager.Persistence;

/// <summary>
/// <c>archive/settings.json</c>, <c>archive/sets/&lt;id&gt;.json</c> and
/// <c>archive/packages/&lt;id&gt;.json</c>, each written atomically. An unreadable document is moved
/// aside, as a job's is, and treated as absent.
/// </summary>
public sealed class JsonArchiveStore(StoragePaths paths, Action<string>? log = null, ArchiveStoreOptions? options = null) : IArchiveStore
{
    private readonly string _settingsFile = Path.Combine(paths.Root, "archive", "settings.json");
    private readonly string _setsDirectory = Path.Combine(paths.Root, "archive", "sets");
    private readonly string _packagesDirectory = Path.Combine(paths.Root, "archive", "packages");
    private readonly ArchiveStoreOptions _options = options ?? new ArchiveStoreOptions();
    private IReadOnlyCollection<Guid> _unreadableSets = [];
    private IReadOnlyCollection<Guid> _unreadablePackagePasswords = [];

    public IReadOnlyCollection<Guid> UnreadableSets => _unreadableSets;

    public IReadOnlyCollection<Guid> UnreadablePackagePasswords => _unreadablePackagePasswords;

    public async Task<ArchiveSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFile)) return null;

        // Unlike a set, nothing can stand in for these. Carrying on as if none were saved would overwrite the user's choices.
        var read = await ReadAsync<ArchiveSettings>(_settingsFile, cancellationToken).ConfigureAwait(false);
        if (read.HeldOpen) throw new IOException("The archive settings can't be read right now.");
        var settings = read.Value;
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
        var unreadable = new List<Guid>();
        _unreadableSets = unreadable;
        if (!Directory.Exists(_setsDirectory)) return sets;
        foreach (var file in Directory.EnumerateFiles(_setsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await ReadAsync<ArchiveSetRecord>(file, cancellationToken).ConfigureAwait(false);
            if (read.HeldOpen && Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var heldId)) unreadable.Add(heldId);
            if (read.Value is not { } set) continue;
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
        var unreadable = new List<Guid>();
        _unreadablePackagePasswords = unreadable;
        if (!Directory.Exists(_packagesDirectory)) return passwords;
        foreach (var file in Directory.EnumerateFiles(_packagesDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await ReadAsync<ArchivePackagePassword>(file, cancellationToken).ConfigureAwait(false);
            if (read.HeldOpen && Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var heldId)) unreadable.Add(heldId);
            if (read.Value is { } password)
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

    /// <summary>
    /// Only what was read and turned out to be bad is moved aside. A file that can't be opened right now, because a
    /// scanner has it, says nothing about what is in it, and losing a finished set's record would run it all again.
    /// </summary>
    private async Task<(T? Value, bool HeldOpen)> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                return (JsonSerializer.Deserialize<T>(json, PersistenceJson.Options) ?? throw new InvalidDataException("The document was empty."), false);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                Quarantine(path, exception.GetType().Name);
                return (null, false);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return (null, false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= _options.ReadAttempts)
                {
                    log?.Invoke($"Could not read archive document '{Path.GetFileName(path)}', so it is left as it is: {exception.GetType().Name}.");
                    return (null, true);
                }

                await Task.Delay(_options.ReadRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
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

public sealed record ArchiveStoreOptions
{
    /// <summary>Tries at a document that can't be opened, counting the first.</summary>
    public int ReadAttempts { get; init; } = 4;

    /// <summary>The wait before the second try, and it grows by this much each time.</summary>
    public TimeSpan ReadRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}
