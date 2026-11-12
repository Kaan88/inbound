namespace DownloadManager.Contracts;

/// <summary>The words an add request may use for its start mode, and the one each stands for.</summary>
public static class StartModes
{
    public const string StartNow = "startNow";
    public const string Queue = "queue";
    public const string Later = "later";

    /// <summary>
    /// The canonical name for <paramref name="value"/> ("startNow", "queue" or "later"), or null when it is not a
    /// start mode. Nothing given means start now. "now" and "start" are what the CLI, the window and the browser
    /// extensions send; numbers and anything else are refused rather than read as "start now".
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return StartNow;
        return value.Trim().ToLowerInvariant() switch
        {
            "startnow" or "now" or "start" => StartNow,
            "queue" or "queued" => Queue,
            "later" => Later,
            _ => null,
        };
    }
}
