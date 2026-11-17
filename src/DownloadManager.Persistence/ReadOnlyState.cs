using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DownloadManager.Core;

namespace DownloadManager.Persistence;

/// <summary>
/// Reads the job list and the settings from a state folder without writing anything: no folder is created, nothing is migrated,
/// repaired, quarantined or saved, and no secret is opened. For a caller that must not start the Host (the Android boot notice
/// counts what would resume). The stores write each file as a temporary file plus an atomic rename, so a read sees a whole old or
/// a whole new file; a file that can't be read or parsed is skipped, and the Host deals with it at its own start.
/// </summary>
public static class ReadOnlyState
{
    /// <summary>Every job file that parses as a job this build understands. Header secrets stay sealed (nothing here needs them).</summary>
    public static IReadOnlyList<DownloadJob> ReadJobs(string stateRoot, Action<string>? log = null)
    {
        var jobs = new List<DownloadJob>();
        var directory = new StoragePaths(stateRoot).JobsDirectory;
        if (!Directory.Exists(directory)) return jobs;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var job = JsonSerializer.Deserialize(File.ReadAllText(file), (JsonTypeInfo<DownloadJob>)PersistenceJson.Options.GetTypeInfo(typeof(DownloadJob)));
                if (job is null || job.SchemaVersion > DownloadJob.CurrentSchemaVersion || job.Id == Guid.Empty) continue;
                jobs.Add(job);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"job '{Path.GetFileName(file)}' skipped: {ex.GetType().Name}");
            }
        }

        return jobs;
    }

    /// <summary>The saved settings, or a new install's when there is no readable file (nothing is written either way).</summary>
    public static AppSettings ReadSettings(string stateRoot, Action<string>? log = null)
    {
        var file = new StoragePaths(stateRoot).SettingsFile;
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize(File.ReadAllText(file), (JsonTypeInfo<AppSettings>)PersistenceJson.Options.GetTypeInfo(typeof(AppSettings))) ?? AppSettings.NewInstall();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"settings unreadable, defaults used: {ex.GetType().Name}");
        }

        return AppSettings.NewInstall();
    }
}
