using System.Text.Json;
using DownloadManager.Core.Providers;

namespace DownloadManager.Persistence;

/// <summary>
/// One file per remote job in <c>remote/jobs</c>, plus one pacing file per account, written like
/// every other durable file. Records only hold ciphertext for their secrets.
/// </summary>
public sealed class JsonRemoteJobStore : IRemoteJobStore
{
    private const int ReadAttempts = 4;

    private readonly StoragePaths _paths;
    private readonly Action<string>? _log;
    private readonly TimeSpan _readRetryDelay;

    public JsonRemoteJobStore(StoragePaths paths, Action<string>? log = null, TimeSpan? readRetryDelay = null)
    {
        _paths = paths;
        _log = log;
        _readRetryDelay = readRetryDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<IReadOnlyList<RemoteJobRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var records = new List<RemoteJobRecord>();
        foreach (var file in Directory.EnumerateFiles(_paths.RemoteJobsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await ReadPatientlyAsync(file, cancellationToken).ConfigureAwait(false);
            if (json is null)
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<RemoteJobRecord>(json, PersistenceJson.Options)
                    ?? throw new InvalidDataException("The remote job file was empty.");
                if (record.SchemaVersion > RemoteJobRecord.CurrentSchemaVersion)
                {
                    throw new InvalidDataException($"Unsupported remote job schema {record.SchemaVersion}.");
                }

                if (record.Id == Guid.Empty || string.IsNullOrWhiteSpace(record.Provider) ||
                    !Path.GetFileNameWithoutExtension(file).Equals(record.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The remote job file doesn't match its name.");
                }

                records.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // The type only: a JsonException's text can quote the file.
                _log?.Invoke($"Could not load remote job '{Path.GetFileName(file)}': {ex.GetType().Name}.");
                Quarantine(file);
            }
        }

        return records;
    }

    /// <summary>
    /// Trouble reading a file says nothing about what is in it. Antivirus or a backup can hold it for
    /// a moment, and a good record must not be set aside as damaged for that. One that stays
    /// unreadable stays where it is, and the next start tries again.
    /// </summary>
    /// <returns>Null when it can't be read.</returns>
    private async Task<string?> ReadPatientlyAsync(string file, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    _log?.Invoke($"Could not read remote job '{Path.GetFileName(file)}': {ex.GetType().Name}. It stays on disk, and the next start tries again.");
                    return null;
                }

                await Task.Delay(_readRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task SaveAsync(RemoteJobRecord record, CancellationToken cancellationToken = default)
        => AtomicFile.WriteAsync(RecordPath(record.Id), JsonSerializer.Serialize(record, PersistenceJson.Options), cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        File.Delete(RecordPath(id));
        return Task.CompletedTask;
    }

    public async Task<RemoteAccountPacing> LoadPacingAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var path = PacingPath(providerId);
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new RemoteAccountPacing();
        }

        try
        {
            return JsonSerializer.Deserialize<RemoteAccountPacing>(json, PersistenceJson.Options) ?? new RemoteAccountPacing();
        }
        catch (JsonException ex)
        {
            _log?.Invoke($"Could not load the pacing of {providerId}: {ex.GetType().Name}.");
            Quarantine(path);
            return new RemoteAccountPacing();
        }
    }

    public Task SavePacingAsync(string providerId, RemoteAccountPacing pacing, CancellationToken cancellationToken = default)
        => AtomicFile.WriteAsync(PacingPath(providerId), JsonSerializer.Serialize(pacing, PersistenceJson.Options), cancellationToken);

    private string RecordPath(Guid id) => Path.Combine(_paths.RemoteJobsDirectory, $"{id:N}.json");

    private string PacingPath(string providerId)
    {
        if (string.IsNullOrEmpty(providerId) || !providerId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("Not a provider id.", nameof(providerId));
        }

        return Path.Combine(_paths.RemoteDirectory, $"pacing-{providerId}.json");
    }

    private void Quarantine(string path)
    {
        try
        {
            _paths.EnsureCreated();
            File.Move(path, Path.Combine(_paths.CorruptDirectory, $"remote-{Path.GetFileName(path)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place, and skipped again at the next start.
        }
    }
}
