using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Persistence;

public sealed class StoragePaths
{
    public StoragePaths(string root)
    {
        Root = root;
        JobsDirectory = Path.Combine(root, "jobs");
        CheckpointsDirectory = Path.Combine(root, "checkpoints");
        MediaCheckpointsDirectory = Path.Combine(root, "media-checkpoints");
        PartialsDirectory = Path.Combine(root, "partial");
        DedupeDirectory = Path.Combine(root, "dedupe");
        LogsDirectory = Path.Combine(root, "logs");
        CorruptDirectory = Path.Combine(root, "corrupt");
        RemoteDirectory = Path.Combine(root, "remote");
        RemoteJobsDirectory = Path.Combine(RemoteDirectory, "jobs");
        SettingsFile = Path.Combine(root, "settings.json");
        HistoryFile = Path.Combine(root, "history.jsonl");
    }

    public string Root { get; }
    public string JobsDirectory { get; }
    public string CheckpointsDirectory { get; }
    public string MediaCheckpointsDirectory { get; }
    public string PartialsDirectory { get; }
    public string DedupeDirectory { get; }
    public string LogsDirectory { get; }
    public string CorruptDirectory { get; }
    public string RemoteDirectory { get; }
    public string RemoteJobsDirectory { get; }
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
        Directory.CreateDirectory(MediaCheckpointsDirectory);
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
    public static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
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

            File.Move(temp, path, overwrite: true);
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
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<DownloadJob>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var jobs = new List<DownloadJob>();
        foreach (var file in Directory.EnumerateFiles(_paths.JobsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var job = JsonSerializer.Deserialize<DownloadJob>(json, PersistenceJson.Options);
                if (job is null)
                {
                    throw new InvalidDataException("The job document was empty.");
                }

                if (job.SchemaVersion > DownloadJob.CurrentSchemaVersion)
                {
                    throw new InvalidDataException($"Unsupported job schema {job.SchemaVersion}.");
                }

                if (job.Id == Guid.Empty || string.IsNullOrWhiteSpace(job.Url))
                {
                    throw new InvalidDataException("The job document is missing its id or URL.");
                }

                NormalizeJob(job);
                jobs.Add(job);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
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
        catch (IOException)
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

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var checkpoint = JsonSerializer.Deserialize<DownloadCheckpoint>(json, PersistenceJson.Options);
            if (checkpoint is null || checkpoint.JobId != jobId ||
                checkpoint.SchemaVersion > DownloadCheckpoint.CurrentSchemaVersion)
            {
                return null;
            }

            return checkpoint;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
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

public sealed class JsonMediaCheckpointStore : IMediaCheckpointStore
{
    private readonly StoragePaths _paths;

    public JsonMediaCheckpointStore(StoragePaths paths)
    {
        _paths = paths;
    }

    public async Task<MediaCheckpoint?> LoadAsync(
        Guid jobId,
        string track,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(jobId, track);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var checkpoint = JsonSerializer.Deserialize<MediaCheckpoint>(json, PersistenceJson.Options);
            return checkpoint is not null &&
                   checkpoint.SchemaVersion <= MediaCheckpoint.CurrentSchemaVersion &&
                   checkpoint.JobId == jobId &&
                   string.Equals(checkpoint.Track, track, StringComparison.Ordinal)
                ? checkpoint
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(MediaCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        checkpoint.SchemaVersion = MediaCheckpoint.CurrentSchemaVersion;
        checkpoint.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(checkpoint, PersistenceJson.Options);
        await AtomicFile.WriteAsync(PathFor(checkpoint.JobId, checkpoint.Track), json, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid jobId, string? track = null, CancellationToken cancellationToken = default)
    {
        var directory = _paths.MediaCheckpointsDirectory;
        if (!Directory.Exists(directory))
        {
            return Task.CompletedTask;
        }

        var pattern = track is null ? $"{jobId:N}-*.json" : Path.GetFileName(PathFor(jobId, track));
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        return Task.CompletedTask;
    }

    private string PathFor(Guid jobId, string track)
        => Path.Combine(_paths.MediaCheckpointsDirectory, $"{jobId:N}-{SafeTrack(track)}.json");

    private static string SafeTrack(string track)
    {
        var clean = new string(track.Where(char.IsLetterOrDigit).ToArray());
        return clean.Length == 0 ? "media" : clean;
    }
}

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly StoragePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(StoragePaths paths)
    {
        _paths = paths;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        if (!File.Exists(_paths.SettingsFile))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_paths.SettingsFile, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, PersistenceJson.Options) ?? new AppSettings();
            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Unsupported settings schema {settings.SchemaVersion}.");
            }

            // Saved by a build from before provider links were recognised. Filtering them in memory
            // would leave the account key on disk until the next unrelated settings change.
            var heldProviderLink = settings.RecentUrls?.Any(ProviderLinks.IsProviderLink) == true;
            Normalize(settings);
            if (!string.IsNullOrEmpty(settings.Proxy.PasswordProtected))
            {
                settings.Proxy.Password = SecretProtector.UnprotectProxy(settings.Proxy.PasswordProtected);
            }

            if (heldProviderLink)
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

            var defaults = new AppSettings();
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
            settings.Proxy.PasswordProtected = string.IsNullOrEmpty(settings.Proxy.Password)
                ? null
                : SecretProtector.ProtectProxy(settings.Proxy.Password);
            var json = JsonSerializer.Serialize(settings, PersistenceJson.Options);
            await AtomicFile.WriteAsync(_paths.SettingsFile, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static readonly (string Id, string Name)[] BuiltInCategoryFolders =
    [
        ("compressed", "Compressed"), ("documents", "Documents"), ("music", "Music"),
        ("programs", "Programs"), ("video", "Video"),
    ];

    private static void Normalize(AppSettings settings)
    {
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 16);
        settings.DefaultConnections = Math.Clamp(settings.DefaultConnections, 1, 32);
        settings.MaxConnectionsPerHost = Math.Clamp(settings.MaxConnectionsPerHost, 1, 64);
        settings.ConnectionProfiles ??= new List<ConnectionProfile>();
        settings.ConnectionProfiles = settings.ConnectionProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Pattern))
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
        settings.FfmpegPath = string.IsNullOrWhiteSpace(settings.FfmpegPath)
            ? null
            : settings.FfmpegPath.Trim();
        settings.PostDownloadCommand = string.IsNullOrWhiteSpace(settings.PostDownloadCommand)
            ? null
            : settings.PostDownloadCommand.Trim();
        if (string.IsNullOrWhiteSpace(settings.UpdateManifestUrl) &&
            string.IsNullOrWhiteSpace(settings.UpdatePublicKey))
        {
            settings.UpdateManifestUrl = ReleaseUpdates.ManifestUrl;
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

        settings.EventSoundFiles ??= new(StringComparer.OrdinalIgnoreCase);
        settings.CustomCategories ??= new();
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

        settings.CategoryFolders = categories;
        var root = settings.DefaultDownloadDirectory;
        if (!string.Equals(settings.CategoryFoldersRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            var previous = settings.CategoryFoldersRoot;
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

        settings.RecentUrls ??= new List<string>();
        settings.RecentUrls = settings.RecentUrls
            .Where(value => !string.IsNullOrWhiteSpace(value) &&
                            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                            uri.Scheme is "http" or "https" or "ftp" &&
                            !ProviderLinks.IsProviderLink(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        settings.SiteLogins ??= new List<SiteLogin>();
        settings.SiteLogins.RemoveAll(login =>
            string.IsNullOrWhiteSpace(login.Site) || string.IsNullOrWhiteSpace(login.Username));
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
        if (settings.Queues.Count == 0)
        {
            settings.Queues.Add(new DownloadQueue
            {
                Id = 0,
                Name = "Main",
                MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 16),
                IsRunning = true,
                Order = 0,
            });
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

        settings.ExcludedFileExtensions ??= new List<string>();
        settings.ExcludedHosts ??= new List<string>();
        settings.InterceptedFileExtensions ??= new List<string>();
        foreach (var list in new[] { settings.ExcludedFileExtensions, settings.ExcludedHosts, settings.InterceptedFileExtensions })
        {
            for (var i = 0; i < list.Count; i++)
            {
                list[i] = list[i].Trim().TrimStart('.').ToLowerInvariant();
            }

            list.RemoveAll(string.IsNullOrWhiteSpace);
            var distinct = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            list.Clear();
            list.AddRange(distinct);
        }
    }
}
