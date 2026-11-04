using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Core;

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

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            candidates.Add(Path.Combine(local, "DownloadManager"));
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            candidates.Add(Path.Combine(roaming, "DownloadManager"));
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

                Migrate(job);
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

            Migrate(job);
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
        job.Touch();
        var json = JsonSerializer.Serialize(job, PersistenceJson.Options);
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

    private static void Migrate(DownloadJob job)
    {
        // Version 1 is the first persisted schema. Future migrations go here.
        job.SchemaVersion = DownloadJob.CurrentSchemaVersion;
        job.RequestHeaders ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        job.CreatedAt = job.CreatedAt == default ? DateTimeOffset.UtcNow : job.CreatedAt;
        job.UpdatedAt = job.UpdatedAt == default ? DateTimeOffset.UtcNow : job.UpdatedAt;
    }

    private string JobPath(Guid id) => Path.Combine(_paths.JobsDirectory, $"{id:N}.json");

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

public sealed class JsonGrabberProjectStore : IGrabberProjectStore
{
    private readonly StoragePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonGrabberProjectStore(StoragePaths paths)
    {
        _paths = paths;
    }

    private string FilePath => Path.Combine(_paths.Root, "grabber-projects.json");

    public async Task<IReadOnlyList<GrabberProject>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return Array.Empty<GrabberProject>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            var projects = JsonSerializer.Deserialize<List<GrabberProject>>(json, PersistenceJson.Options);
            if (projects is null)
            {
                return Array.Empty<GrabberProject>();
            }

            foreach (var project in projects)
            {
                project.Extensions ??= new List<string>();
                project.IncludePatterns ??= new List<string>();
                project.ExcludePatterns ??= new List<string>();
            }

            return projects;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return Array.Empty<GrabberProject>();
        }
    }

    public async Task SaveAsync(GrabberProject project, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var projects = (await LoadAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = projects.FindIndex(existing => existing.Id == project.Id);
            if (index >= 0)
            {
                projects[index] = project.Clone();
            }
            else
            {
                projects.Add(project.Clone());
            }

            _paths.EnsureCreated();
            var json = JsonSerializer.Serialize(projects, PersistenceJson.Options);
            await AtomicFile.WriteAsync(FilePath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var projects = (await LoadAllAsync(cancellationToken).ConfigureAwait(false))
                .Where(project => project.Id != id)
                .ToList();
            var json = JsonSerializer.Serialize(projects, PersistenceJson.Options);
            await AtomicFile.WriteAsync(FilePath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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

            Normalize(settings);
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
            var json = JsonSerializer.Serialize(settings, PersistenceJson.Options);
            await AtomicFile.WriteAsync(_paths.SettingsFile, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
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
