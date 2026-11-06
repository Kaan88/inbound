using System.Globalization;
using System.Text.Json;

namespace DownloadManager.Runtime;

public sealed record UpdateInstallOutcome(bool Succeeded, string Version, string? Error, DateTimeOffset At);

/// <summary>
/// What became of the last update. The helper script does its work after the Host has exited, so
/// the next Host is the first thing able to tell the user. The helper writes the result file; the
/// pending file names the helper, which also catches one that died without writing anything.
/// </summary>
public sealed class UpdateInstallJournal
{
    private readonly string _pendingPath;
    private readonly string _reportedPath;

    public UpdateInstallJournal(string updatesDirectory)
    {
        ResultPath = Path.Combine(updatesDirectory, "install-result.json");
        _pendingPath = Path.Combine(updatesDirectory, "install-pending.json");
        _reportedPath = Path.Combine(updatesDirectory, "install-result.reported");
    }

    public string ResultPath { get; }

    /// <summary>Forgets the previous update, so its result cannot be taken for this one's.</summary>
    public void ClearForNewInstall()
    {
        File.Delete(ResultPath);
        File.Delete(_pendingPath);
        File.Delete(_reportedPath);
    }

    /// <summary>Best effort: without it, only a helper that dies silently goes unnoticed.</summary>
    public void MarkPending(string version, int helperProcessId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath)!);
            File.WriteAllText(_pendingPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = version,
                ["helperProcessId"] = helperProcessId,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>True while the helper named by the pending file is still at work.</summary>
    public bool HelperStillRunning(Func<int, bool>? isHelperRunning = null) =>
        TryReadPending(out _, out var helperId) && (isHelperRunning ?? IsHelperRunning)(helperId);

    /// <summary>
    /// The outcome not yet told to the user, once. A later result counts as new. Null while the
    /// helper is still working.
    /// </summary>
    public UpdateInstallOutcome? TakeUnreported(Func<int, bool>? isHelperRunning = null, DateTimeOffset? now = null)
    {
        var outcome = Read();
        if (outcome is null)
        {
            if (!TryReadPending(out var version, out var helperId)) return null;
            if ((isHelperRunning ?? IsHelperRunning)(helperId)) return null;
            outcome = new UpdateInstallOutcome(false, version,
                "The update stopped before it finished.", now ?? DateTimeOffset.Now);
            Write(outcome);
        }
        File.Delete(_pendingPath);

        var at = outcome.At.ToString("O", CultureInfo.InvariantCulture);
        if (File.Exists(_reportedPath) && ParseTime(File.ReadAllText(_reportedPath)) == outcome.At) return null;
        File.WriteAllText(_reportedPath, at);
        return outcome;
    }

    /// <summary>
    /// True while an automatic retry of this version would be too soon. A failed update restarts
    /// the Host, which checks for updates as it starts; retrying at once would quit the app again.
    /// </summary>
    public bool FailedRecently(string version, TimeSpan window, DateTimeOffset? now = null)
    {
        var outcome = Read();
        return outcome is { Succeeded: false } &&
            string.Equals(outcome.Version, version, StringComparison.OrdinalIgnoreCase) &&
            (now ?? DateTimeOffset.Now) - outcome.At < window;
    }

    private UpdateInstallOutcome? Read()
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(ResultPath));
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString();
            var version = root.GetProperty("version").GetString();
            var at = ParseTime(root.GetProperty("at").GetString());
            if (string.IsNullOrWhiteSpace(version) || at is null) return null;
            var error = root.TryGetProperty("error", out var text) ? text.GetString() : null;
            return new UpdateInstallOutcome(string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase),
                version, string.IsNullOrWhiteSpace(error) ? null : error, at.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Write(UpdateInstallOutcome outcome)
    {
        File.WriteAllText(ResultPath, JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["status"] = outcome.Succeeded ? "succeeded" : "failed",
            ["version"] = outcome.Version,
            ["error"] = outcome.Error,
            ["at"] = outcome.At.ToString("O", CultureInfo.InvariantCulture),
        }));
    }

    private bool TryReadPending(out string version, out int helperProcessId)
    {
        version = string.Empty;
        helperProcessId = 0;
        try
        {
            if (!File.Exists(_pendingPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(_pendingPath));
            version = document.RootElement.GetProperty("version").GetString() ?? string.Empty;
            helperProcessId = document.RootElement.GetProperty("helperProcessId").GetInt32();
            return version.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;

    // After a restart its number may belong to another program.
    private static bool IsHelperRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited &&
                string.Equals(process.ProcessName, "powershell", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
