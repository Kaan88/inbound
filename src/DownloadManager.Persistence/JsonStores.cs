using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Core;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Persistence;

public sealed class StoragePaths
{
    public StoragePaths(string root)
    {
        Root = root;
        JobsDirectory = Path.Combine(root, "jobs");
        CheckpointsDirectory = Path.Combine(root, "checkpoints");
        PartialsDirectory = Path.Combine(root, "partial");
        DedupeDirectory = Path.Combine(root, "dedupe");
        LogsDirectory = Path.Combine(root, "logs");
        CorruptDirectory = Path.Combine(root, "corrupt");
        RemoteDirectory = Path.Combine(root, "remote");
        RemoteJobsDirectory = Path.Combine(RemoteDirectory, "jobs");
        TorrentsDirectory = Path.Combine(root, "torrents");
        SettingsFile = Path.Combine(root, "settings.json");
        HistoryFile = Path.Combine(root, "history.jsonl");
    }

    public string Root { get; }
    public string JobsDirectory { get; }
    public string CheckpointsDirectory { get; }
    public string PartialsDirectory { get; }
    public string DedupeDirectory { get; }
    public string LogsDirectory { get; }
    public string CorruptDirectory { get; }
    public string RemoteDirectory { get; }
    public string RemoteJobsDirectory { get; }
    /// <summary>Per torrent job: its metadata, file list and the worker's resume data. Never payload.</summary>
    public string TorrentsDirectory { get; }
    public string SettingsFile { get; }
    public string HistoryFile { get; }

    public static StoragePaths CreateDefault()
    {
        var candidates = new List<string>();
        var overridden = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            candidates.Add(overridden);
        }

        var sandboxProfile = !string.IsNullOrWhiteSpace(overridden) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTANCE"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            candidates.Add(Path.Combine(local, DownloadManager.Contracts.AppFolders.Name));
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            candidates.Add(Path.Combine(roaming, DownloadManager.Contracts.AppFolders.Name));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "data"));

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, $".write-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return new StoragePaths(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A sandbox profile exists to stay away from the real state, so failing is the only safe answer.
                if (sandboxProfile && candidate == overridden) throw;

                // Try the next location. This also covers sandboxed development shells
                // where LocalAppData exists but is not writable by child processes.
            }
        }

        return new StoragePaths(Path.Combine(AppContext.BaseDirectory, "data"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(JobsDirectory);
        Directory.CreateDirectory(CheckpointsDirectory);
        Directory.CreateDirectory(PartialsDirectory);
        Directory.CreateDirectory(DedupeDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CorruptDirectory);
        Directory.CreateDirectory(RemoteDirectory);
        Directory.CreateDirectory(RemoteJobsDirectory);
    }
}

internal static class PersistenceJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal static class AtomicFile
{
    /// <summary>
    /// Deletes temporary files <see cref="WriteAsync"/> left behind when the process was killed between creating and
    /// moving one. Only ".name.guid.tmp" files older than <paramref name="olderThan"/>, so a write in progress in
    /// another process (the web UI bridge shares this folder) is left alone. Best effort.
    /// </summary>
    public static int SweepStaleTemps(string directory, TimeSpan olderThan)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var file in Directory.EnumerateFiles(directory, ".*.tmp"))
            {
                if (!IsAtomicTemp(Path.GetFileName(file))) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held or protected: the next start tries again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder can't be listed right now.
        }

        return removed;
    }

    /// <summary>".{name}.{32 hex}.tmp", the shape <see cref="WriteAsync"/> gives its temporary files.</summary>
    internal static bool IsAtomicTemp(string fileName)
    {
        if (!fileName.StartsWith('.') || !fileName.EndsWith(".tmp", StringComparison.Ordinal)) return false;
        var stem = fileName[..^4];
        var dot = stem.LastIndexOf('.');
        if (dot <= 0) return false;
        var id = stem[(dot + 1)..];
        return id.Length == 32 && id.All(Uri.IsHexDigit);
    }

    public static Task WriteAsync(string path, string content, CancellationToken cancellationToken)
        => WriteBytesAsync(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content), cancellationToken);

    public static async Task WriteBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await ReplaceAsync(temp, path).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    /// <summary>A scanner or indexer can hold the old file for a moment, and that shouldn't cost a save.</summary>
    private static async Task ReplaceAsync(string temp, string path)
    {
        const int MostAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < MostAttempts)
            {
                await Task.Delay(20 << attempt, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}

public sealed class JsonJobStore : IJobStore
{
    private readonly StoragePaths _paths;
    private readonly Action<string>? _log;

    public JsonJobStore(StoragePaths paths, Action<string>? log = null)
    {
        _paths = paths;
        _log = log;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var removed = 0;
        // Every folder of the state root that an atomic write saves into (the web sign-in files live in the root).
        foreach (var directory in new[]
                 {
                     _paths.Root, _paths.JobsDirectory, _paths.CheckpointsDirectory,
                     _paths.DedupeDirectory, _paths.RemoteDirectory, _paths.RemoteJobsDirectory, _paths.TorrentsDirectory,
                     Path.Combine(_paths.Root, "archive"), Path.Combine(_paths.Root, "archive", "sets"),
                     Path.Combine(_paths.Root, "archive", "packages"), Path.Combine(_paths.Root, "updates"),
                 })
        {
            removed += AtomicFile.SweepStaleTemps(directory, TimeSpan.FromMinutes(1));
        }

        if (removed > 0) _log?.Invoke($"Removed {removed} temporary file(s) left by an interrupted save.");
        RemoveRetiredStreamCheckpoints();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stream (HLS/DASH) downloading was cut in 0.7.2, and with it the fragment checkpoints an older build kept in
    /// "media-checkpoints". Nothing reads them any more, so the folder goes. Best effort: a held file stays for the next start.
    /// </summary>
    private void RemoveRetiredStreamCheckpoints()
    {
        var directory = Path.Combine(_paths.Root, "media-checkpoints");
        if (!Directory.Exists(directory)) return;
        try
        {
            Directory.Delete(directory, recursive: true);
            _log?.Invoke("Removed the stream download checkpoints an older version left.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not remove the old stream download checkpoints yet: {ex.Message}");
        }
    }

    private const int ReadAttempts = 4;

    /// <summary>
    /// A scanner or indexer can hold a job file for a moment; that is no reason to set the job aside.
    /// Null when the file is gone or still can't be read, which leaves it on disk for the next start.
    /// </summary>
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
                    _log?.Invoke($"Could not read job '{Path.GetFileName(file)}': {ex.GetType().Name}. It stays on disk, and the next start tries again.");
                    return null;
                }

                await Task.Delay(100 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<DownloadJob>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var jobs = new List<DownloadJob>();
        foreach (var file in Directory.EnumerateFiles(_paths.JobsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await ReadPatientlyAsync(file, cancellationToken).ConfigureAwait(false);
            if (json is null)
            {
                continue;
            }

            try
            {
                var job = JsonSerializer.Deserialize<DownloadJob>(json, PersistenceJson.Options);
                if (job is null)
                {
                    throw new InvalidDataException("The job document was empty.");
                }

                if (job.SchemaVersion > DownloadJob.CurrentSchemaVersion)
                {
                    // A newer build's job, after a downgrade. Setting it aside would leave its kept checkpoint and part
                    // with no job, while settings and checkpoints from that build are kept; it stays for that build.
                    _log?.Invoke($"Job '{Path.GetFileName(file)}' was saved by a newer version of the app (schema {job.SchemaVersion}). It stays on disk for that version.");
                    continue;
                }

                if (job.Id == Guid.Empty || string.IsNullOrWhiteSpace(job.Url))
                {
                    throw new InvalidDataException("The job document is missing its id or URL.");
                }

                NormalizeJob(job);
                MarkLegacyPauseAsTheUsers(job, json);
                jobs.Add(job);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Only content that can't be a job is set aside; a file that couldn't be read stays (above).
                Quarantine(file);
                _log?.Invoke($"Could not load job '{Path.GetFileName(file)}': {ex.Message}");
            }
        }

        return jobs;
    }

    public async Task<DownloadJob?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var path = JobPath(jobId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var job = JsonSerializer.Deserialize<DownloadJob>(json, PersistenceJson.Options);
            if (job is null || job.SchemaVersion > DownloadJob.CurrentSchemaVersion || job.Id != jobId)
            {
                return null;
            }

            NormalizeJob(job);
            MarkLegacyPauseAsTheUsers(job, json);
            return job;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log?.Invoke($"Could not load job '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    public async Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        if (job.Id == Guid.Empty)
        {
            throw new ArgumentException("The job id is empty.", nameof(job));
        }

        job.SchemaVersion = DownloadJob.CurrentSchemaVersion;
        job.CompletionOptions ??= new();
        job.Touch();
        // Protect a detached snapshot; the engine continues using plaintext in
        // memory, and a failed write cannot mutate its live request headers.
        var stored = job.Clone();
        stored.SchemaVersion = stored.SchemaVersionToWrite;
        stored.RequestHeadersProtected.Clear();
        foreach (var (name, value) in stored.RequestHeaders.ToList())
        {
            if (!IsSensitiveHeader(name)) continue;
            stored.RequestHeadersProtected[name] = SecretProtector.ProtectHeader(value);
            stored.RequestHeaders.Remove(name);
        }
        // Already ciphertext only when it could not be decrypted at load; keep it as it is.
        if (!string.IsNullOrEmpty(stored.ProtectedUrl) && !SecretProtector.IsProtected(stored.ProtectedUrl))
            stored.ProtectedUrl = SecretProtector.ProtectUrl(stored.ProtectedUrl);
        var json = JsonSerializer.Serialize(stored, PersistenceJson.Options);
        var path = JobPath(job.Id);
        await AtomicFile.WriteAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var path = JobPath(jobId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task<Guid?> FindClaimedRequestAsync(
        string extensionId,
        string clientRequestId,
        CancellationToken cancellationToken = default)
    {
        var path = ClaimPath(extensionId, clientRequestId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var claim = JsonSerializer.Deserialize<RequestClaim>(json, PersistenceJson.Options);
            return claim?.JobId;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log?.Invoke($"Could not read request claim: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> TryClaimRequestAsync(
        string extensionId,
        string clientRequestId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(extensionId) || string.IsNullOrWhiteSpace(clientRequestId))
        {
            throw new ArgumentException("Extension id and request id are required.");
        }

        _paths.EnsureCreated();
        var path = ClaimPath(extensionId, clientRequestId);
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var claim = new RequestClaim
        {
            JobId = jobId,
            ExtensionId = extensionId,
            ClientRequestId = clientRequestId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(claim, PersistenceJson.Options);
        try
        {
            // Write the claim to a unique temp, then attempt an atomic no-overwrite move.
            var bytes = new UTF8Encoding(false).GetBytes(json);
            await using (var stream = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    public Task DeleteClaimAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default)
    {
        var path = ClaimPath(extensionId, clientRequestId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A job saved paused before <see cref="DownloadJob.PausedByUser"/> existed carries no flag, and which pause it was
    /// is not known. It is taken as the user's, so starting its queue never resumes it; its own Resume still does (U-WL-2).
    /// </summary>
    private static void MarkLegacyPauseAsTheUsers(DownloadJob job, string json)
    {
        if (job.State != DownloadState.Paused || job.PausedByUser) return;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, nameof(DownloadJob.PausedByUser), StringComparison.OrdinalIgnoreCase)) return;
        }

        job.PausedByUser = true;
    }

    private static void NormalizeJob(DownloadJob job)
    {
        job.SchemaVersion = DownloadJob.CurrentSchemaVersion;
        job.RequestHeaders ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        job.RequestHeaders = new Dictionary<string, string>(job.RequestHeaders, StringComparer.OrdinalIgnoreCase);
        job.RequestHeadersProtected ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, protectedValue) in job.RequestHeadersProtected)
        {
            if (!IsSensitiveHeader(name)) continue;
            job.RequestHeaders.Remove(name);
            var value = SecretProtector.UnprotectHeader(protectedValue);
            if (value is not null) job.RequestHeaders[name] = value;
        }
        // Unreadable ciphertext stays in place: a run can then say why it cannot start, and a
        // later save does not destroy it. Unlike a lost header, this address has no fallback.
        job.ProtectedUrl = SecretProtector.UnprotectUrl(job.ProtectedUrl) ?? job.ProtectedUrl;
        job.CreatedAt =job.CreatedAt == default ? DateTimeOffset.UtcNow : job.CreatedAt;
        job.UpdatedAt = job.UpdatedAt == default ? DateTimeOffset.UtcNow : job.UpdatedAt;
    }

    private string JobPath(Guid id) => Path.Combine(_paths.JobsDirectory, $"{id:N}.json");

    private static bool IsSensitiveHeader(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

    private string ClaimPath(string extensionId, string clientRequestId)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(extensionId + "\n" + clientRequestId))).ToLowerInvariant();
        return Path.Combine(_paths.DedupeDirectory, $"{hash}.json");
    }

    private void Quarantine(string path)
    {
        try
        {
            _paths.EnsureCreated();
            var destination = Path.Combine(_paths.CorruptDirectory, $"{Path.GetFileName(path)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
            File.Move(path, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the original in place if quarantine fails.
        }
    }

    private sealed class RequestClaim
    {
        public Guid JobId { get; set; }
        public string ExtensionId { get; set; } = string.Empty;
        public string ClientRequestId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}

public sealed class JsonCheckpointStore : IDownloadCheckpointStore
{
    private const int LoadAttempts = 4;

    private readonly StoragePaths _paths;

    public JsonCheckpointStore(StoragePaths paths)
    {
        _paths = paths;
    }

    public async Task<DownloadCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(jobId);
        if (!File.Exists(path))
        {
            return null;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var checkpoint = JsonSerializer.Deserialize<DownloadCheckpoint>(json, PersistenceJson.Options);
                if (checkpoint is not null && checkpoint.SchemaVersion > DownloadCheckpoint.CurrentSchemaVersion)
                {
                    // Null would tell the transfer to start over and truncate the part; after a downgrade that costs
                    // every partial download. Failing keeps the file and the bytes for the newer build.
                    throw new CheckpointTooNewException(checkpoint.SchemaVersion);
                }

                if (checkpoint is null)
                {
                    throw new CheckpointDamagedException();
                }

                if (checkpoint.JobId != jobId)
                {
                    return null;
                }

                return checkpoint;
            }
            catch (JsonException)
            {
                // Null here would truncate the part just like a missing file; atomic writes make this a damaged disk
                // or a hand edit, which a retry won't mend. Keep the file and the bytes and say so.
                throw new CheckpointDamagedException();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException &&
                                       ex is not CheckpointUnusableException && attempt < LoadAttempts)
            {
                await Task.Delay(100 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task SaveAsync(DownloadCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var json = JsonSerializer.Serialize(checkpoint, PersistenceJson.Options);
        await AtomicFile.WriteAsync(PathFor(checkpoint.JobId), json, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(jobId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(Guid jobId)
        => Path.Combine(_paths.CheckpointsDirectory, $"{jobId:N}.json");
}

/// <summary>A checkpoint written by a newer build; the part is kept for it.</summary>
public sealed class CheckpointTooNewException(int schemaVersion)
    : CheckpointUnusableException("This download's saved progress was written by a newer version of the app. Update the app to continue it; the downloaded part was kept.")
{
    public int SchemaVersion { get; } = schemaVersion;
}

/// <summary>A checkpoint whose content can't be read as one; the file and the part are kept.</summary>
public sealed class CheckpointDamagedException()
    : CheckpointUnusableException("This download's saved progress is damaged, so it was not started and the downloaded part was kept. To start over, delete the download and add it again.");

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly StoragePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(StoragePaths paths)
    {
        _paths = paths;
    }

    /// <summary>Written beside the settings by any save from a build with the PERF.5 default; see <see cref="LoadAsync"/>.</summary>
    private string CloseWindowDefaultMarker => _paths.SettingsFile + ".perf5";

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        if (!File.Exists(_paths.SettingsFile))
        {
            var defaults = AppSettings.NewInstall();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_paths.SettingsFile, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, PersistenceJson.Options) ?? new AppSettings();
            // Saved by a newer build (a downgrade). Resetting every setting would cost the user their folders, limits
            // and logins for one version step, so the settings this build knows are kept, and the newer file is kept
            // whole beside them in the corrupt folder. The save below writes this build's schema.
            var fromNewerBuild = settings.SchemaVersion > AppSettings.CurrentSchemaVersion;
            if (fromNewerBuild)
            {
                try
                {
                    File.Copy(_paths.SettingsFile, Path.Combine(_paths.CorruptDirectory,
                        $"settings.newer-{settings.SchemaVersion}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json"), overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Without a copy, leave the newer file untouched rather than overwrite it.
                    throw new InvalidDataException($"Unsupported settings schema {settings.SchemaVersion}.", ex);
                }
            }

            // Saved by a build from before provider links were recognised. Filtering them in memory
            // would leave the account key on disk until the next unrelated settings change.
            var heldProviderLink = settings.RecentUrls?.Any(ProviderLinks.IsProviderLink) == true;
            // PERF.5 (owner, 2026-11-08): "Closing the window ends it" is on by default, and installs upgraded from a
            // build that stored it off (0.5.3, where it arrived off) get it on once. The marker says these settings
            // were saved by a build that knows the new default, so a later choice of off is kept.
            var closeWindowDefaultPending = !File.Exists(CloseWindowDefaultMarker);
            if (closeWindowDefaultPending)
            {
                settings.CloseWindowCompletely = true;
            }

            // T14: before it, "folders by category" decided for torrents too. Settings saved without the torrent switch
            // keep that choice for torrents, and (with no torrent folder in them) the same folders, so nothing moves.
            var torrentFoldersPending = !HasProperty(json, nameof(AppSettings.UseTorrentCategoryFolders));
            if (torrentFoldersPending)
            {
                settings.UseTorrentCategoryFolders = settings.UseCategoryFolders;
            }

            Normalize(settings);
            if (!string.IsNullOrEmpty(settings.Proxy.PasswordProtected))
            {
                var password = SecretProtector.UnprotectProxy(settings.Proxy.PasswordProtected);
                if (!string.IsNullOrEmpty(password))
                {
                    settings.Proxy.Password = password;
                    // Sealed again from Password at save, so clearing the password really clears it.
                    settings.Proxy.PasswordProtected = null;
                }

                // Otherwise (another user's or machine's blob) it stays sealed, as ProtectedUrl does, and
                // SaveAsync keeps it until a new password is set.
            }

            if (heldProviderLink || closeWindowDefaultPending || torrentFoldersPending || fromNewerBuild)
            {
                await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var quarantine = Path.Combine(_paths.CorruptDirectory,
                $"settings.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            try
            {
                File.Move(_paths.SettingsFile, quarantine, overwrite: true);
            }
            catch (IOException)
            {
                // fall through with defaults
            }

            var defaults = AppSettings.NewInstall();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            Normalize(settings);
            if (!string.IsNullOrEmpty(settings.Proxy.Password))
            {
                settings.Proxy.PasswordProtected = SecretProtector.ProtectProxy(settings.Proxy.Password);
            }
            else if (!SecretProtector.IsProtected(settings.Proxy.PasswordProtected))
            {
                settings.Proxy.PasswordProtected = null;
            }
            var json = JsonSerializer.Serialize(settings, PersistenceJson.Options);
            await AtomicFile.WriteAsync(_paths.SettingsFile, json, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(CloseWindowDefaultMarker))
            {
                await AtomicFile.WriteAsync(CloseWindowDefaultMarker, "1", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<(string Id, string Name)> BuiltInCategoryFolders => TorrentFolderRules.BuiltInFolders;

    /// <summary>Whether the settings file names <paramref name="name"/> at the top level, in any letter case.</summary>
    private static bool HasProperty(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.EnumerateObject().Any(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPublicKey(string pem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    private static void Normalize(AppSettings settings)
    {
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 16);
        settings.DefaultConnections = Math.Clamp(settings.DefaultConnections, 1, 32);
        settings.MaxConnectionsPerHost = Math.Clamp(settings.MaxConnectionsPerHost, 1, 64);
        settings.ConnectionProfiles ??= new List<ConnectionProfile>();
        settings.ConnectionProfiles = settings.ConnectionProfiles
            .Where(profile => profile is not null && !string.IsNullOrWhiteSpace(profile.Pattern))
            .Select(profile => new ConnectionProfile
            {
                Pattern = profile.Pattern.Trim(),
                MaxConnections = Math.Clamp(profile.MaxConnections, 1, 32),
            })
            .Take(50)
            .ToList();
        if (settings.GlobalSpeedLimitBytesPerSecond is <= 0)
        {
            settings.GlobalSpeedLimitBytesPerSecond = null;
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultDownloadDirectory))
        {
            settings.DefaultDownloadDirectory = AppSettings.GetDefaultDownloadDirectory();
        }

        settings.Proxy ??= new ProxySettings();
        settings.Proxy.Mode = settings.Proxy.Mode?.Trim().ToLowerInvariant() switch
        {
            "system" => "system",
            "manual" => "manual",
            _ => "none",
        };
        settings.Proxy.Address = string.IsNullOrWhiteSpace(settings.Proxy.Address)
            ? null
            : settings.Proxy.Address.Trim();
        settings.Proxy.Username = string.IsNullOrWhiteSpace(settings.Proxy.Username)
            ? null
            : settings.Proxy.Username.Trim();
        settings.PostDownloadCommand = string.IsNullOrWhiteSpace(settings.PostDownloadCommand)
            ? null
            : settings.PostDownloadCommand.Trim();
        if (string.IsNullOrWhiteSpace(settings.UpdateManifestUrl) &&
            string.IsNullOrWhiteSpace(settings.UpdatePublicKey))
        {
            settings.UpdateManifestUrl = ReleaseUpdates.ManifestUrl;
            settings.UpdatePublicKey = ReleaseUpdates.PublicKey;
        }
        else if (!string.IsNullOrWhiteSpace(settings.UpdatePublicKey) && !IsPublicKey(settings.UpdatePublicKey))
        {
            // Options in 0.4.0 and 0.5.0 saved only the key's first line, which no update could pass. A key that
            // can't be read verifies nothing, so the built-in one takes its place.
            settings.UpdatePublicKey = ReleaseUpdates.PublicKey;
        }
        settings.UpdateManifestUrl = string.IsNullOrWhiteSpace(settings.UpdateManifestUrl)
            ? null
            : settings.UpdateManifestUrl.Trim();
        settings.UpdateChannel = string.IsNullOrWhiteSpace(settings.UpdateChannel)
            ? "stable"
            : settings.UpdateChannel.Trim().ToLowerInvariant();
        settings.UpdateCheckIntervalHours = Math.Clamp(settings.UpdateCheckIntervalHours, 1, 168);
        settings.SkippedUpdateVersion = string.IsNullOrWhiteSpace(settings.SkippedUpdateVersion)
            ? null
            : settings.SkippedUpdateVersion.Trim();

        settings.Theme = ThemeChoices.Normalize(settings.Theme);
        settings.EventSoundFiles ??= new(StringComparer.OrdinalIgnoreCase);
        settings.CustomCategories ??= new();
        settings.CustomCategories.RemoveAll(category => category is null);
        settings.ArchivedDownloads ??= new();
        settings.ArchivedDownloads = settings.ArchivedDownloads.Where(record => record is not null && record.Id != Guid.Empty).DistinctBy(record => record.Id).ToList();
        settings.DownloadGroups ??= new();
        settings.DownloadGroups = settings.DownloadGroups.Where(group => group is not null && group.Id != Guid.Empty)
            .DistinctBy(group => group.Id).ToList();
        var groupedJobs = new HashSet<Guid>();
        foreach (var group in settings.DownloadGroups)
        {
            group.Name = string.IsNullOrWhiteSpace(group.Name) ? "Downloads" : group.Name;
            group.JobIds = (group.JobIds ?? new()).Where(id => id != Guid.Empty && groupedJobs.Add(id)).ToList();
        }

        settings.CategoryFolders ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.CategoryFolders)
        {
            var key = pair.Key?.Trim().ToLowerInvariant();
            var value = pair.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                categories[key] = value;
            }
        }

        settings.TorrentCategoryFoldersRoot = string.IsNullOrWhiteSpace(settings.TorrentCategoryFoldersRoot)
            ? null
            : settings.TorrentCategoryFoldersRoot.Trim();
        var root = settings.DefaultDownloadDirectory;
        if (!string.Equals(settings.CategoryFoldersRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            var previous = settings.CategoryFoldersRoot;
            // T14: the torrent folder still at Torrents under the old download folder moves with it, like the category folders.
            if (previous is not null && settings.TorrentCategoryFoldersRoot is { } torrentRoot &&
                string.Equals(torrentRoot, TorrentFolderRules.DefaultRoot(previous), StringComparison.OrdinalIgnoreCase))
            {
                settings.TorrentCategoryFoldersRoot = TorrentFolderRules.DefaultRoot(root);
            }

            foreach (var (id, name) in BuiltInCategoryFolders)
            {
                if (previous is null)
                {
                    if (!categories.ContainsKey(id)) categories[id] = Path.Combine(root, name);
                }
                else if (categories.TryGetValue(id, out var current) &&
                         string.Equals(current, Path.Combine(previous, name), StringComparison.OrdinalIgnoreCase))
                {
                    categories[id] = Path.Combine(root, name);
                }
            }

            settings.CategoryFoldersRoot = root;
        }

        // Published once it is complete: a snapshot taken meanwhile (MapSettings) copies it, and must never see it
        // being filled (audit 2 M19).
        settings.CategoryFolders = categories;

        settings.FavoriteFolders = DownloadFolders.Normalize(settings.FavoriteFolders);
        settings.RecentDestinationDirectories = DownloadFolders.NormalizeRecent(settings.RecentDestinationDirectories);
        settings.RecentUrls ??= new List<string>();
        settings.RecentUrls = settings.RecentUrls
            .Where(value => !string.IsNullOrWhiteSpace(value) &&
                            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                            uri.Scheme is "http" or "https" &&
                            !ProviderLinks.IsProviderLink(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        // A new list rather than one changed in place (audit 2 M19).
        settings.SiteLogins = (settings.SiteLogins ?? new List<SiteLogin>()).Where(login =>
            login is not null && !string.IsNullOrWhiteSpace(login.Site) && !string.IsNullOrWhiteSpace(login.Username)).ToList();
        foreach (var login in settings.SiteLogins)
        {
            login.Site = login.Site.Trim();
            login.Username = login.Username.Trim();
            if (login.Id == Guid.Empty)
            {
                login.Id = Guid.NewGuid();
            }
        }

        settings.Queues ??= new List<DownloadQueue>();
        settings.Queues.RemoveAll(queue => queue is null);
        if (settings.Queues.Count == 0)
        {
            settings.Queues =
            [
                new DownloadQueue
                {
                    Id = 0,
                    Name = "Main",
                    MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 16),
                    IsRunning = true,
                    Order = 0,
                },
            ];
        }

        var seenQueueIds = new HashSet<int>();
        for (var i = 0; i < settings.Queues.Count; i++)
        {
            var queue = settings.Queues[i];
            if (!seenQueueIds.Add(queue.Id))
            {
                queue.Id = seenQueueIds.Count == 0 ? 0 : seenQueueIds.Max() + 1;
                seenQueueIds.Add(queue.Id);
            }

            queue.Name = string.IsNullOrWhiteSpace(queue.Name) ? $"Queue {i + 1}" : queue.Name.Trim();
            queue.MaxConcurrentDownloads = Math.Clamp(queue.MaxConcurrentDownloads, 1, 16);
            queue.Order = i;
            queue.QuotaBytes = Math.Clamp(queue.QuotaBytes, 1, 10L * 1024 * 1024 * 1024 * 1024);
            queue.QuotaWindowHours = Math.Clamp(queue.QuotaWindowHours, 1, 24 * 365);
            if (queue.QuotaBytesUsed < 0)
            {
                queue.QuotaBytesUsed = 0;
            }
        }

        if (!settings.Queues.Any(q => q.Id == settings.DefaultQueueId))
        {
            settings.DefaultQueueId = settings.Queues[0].Id;
        }

        settings.Schedules ??= new List<ScheduleRule>();
        settings.Schedules.RemoveAll(schedule => schedule is null);
        foreach (var schedule in settings.Schedules)
        {
            if (!settings.Queues.Any(q => q.Id == schedule.QueueId))
            {
                schedule.QueueId = settings.DefaultQueueId;
            }

            schedule.Name = string.IsNullOrWhiteSpace(schedule.Name) ? "Schedule" : schedule.Name.Trim();
            schedule.Kind = string.Equals(schedule.Kind, "oneTime", StringComparison.OrdinalIgnoreCase)
                ? "oneTime"
                : "daily";
            schedule.Days = string.IsNullOrWhiteSpace(schedule.Days) ? "Mon,Tue,Wed,Thu,Fri,Sat,Sun" : schedule.Days;
            schedule.StartTime = string.IsNullOrWhiteSpace(schedule.StartTime) ? "09:00" : schedule.StartTime;
            schedule.ShutdownMode = schedule.ShutdownMode?.Trim().ToLowerInvariant() switch
            {
                "restart" => "restart",
                "logoff" => "logoff",
                _ => "shutdown",
            };
        }

        settings.ExcludedFileExtensions = NormalizeNames(settings.ExcludedFileExtensions);
        settings.ExcludedHosts = NormalizeNames(settings.ExcludedHosts);
        settings.InterceptedFileExtensions = NormalizeNames(settings.InterceptedFileExtensions);
    }

    /// <summary>
    /// A new list, never the one given changed in place (audit 2 M19): the Host's settings are saved while a snapshot
    /// of them may be read, and a list emptied and refilled here could be read empty there.
    /// </summary>
    private static List<string> NormalizeNames(List<string>? names) => (names ?? new List<string>())
        .Where(name => name is not null)
        .Select(name => name.Trim().TrimStart('.').ToLowerInvariant())
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
