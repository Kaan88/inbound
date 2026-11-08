namespace DownloadManager.Runtime;

/// <summary>
/// The mark Windows and browsers give a file from the internet (the <c>Zone.Identifier</c> stream, zone 3), so
/// SmartScreen and Office warn before it is opened. Written for a download a browser on another device added (D8); the
/// general item is recorded separately.
/// </summary>
public static class MarkOfTheWeb
{
    private const int InternetZone = 3;

    /// <summary>
    /// Marks the file, naming only the site's scheme and host: the full address can carry a token. A file system without
    /// streams (FAT, exFAT, a share that refuses them) keeps the file unmarked, and that is only logged.
    /// </summary>
    public static void TryWrite(string path, string? address, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var host = Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? $"HostUrl={uri.Scheme}://{uri.IdnHost}/" + Environment.NewLine
            : string.Empty;
        try
        {
            File.WriteAllText(path + ":Zone.Identifier",
                "[ZoneTransfer]" + Environment.NewLine + $"ZoneId={InternetZone}" + Environment.NewLine + host);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Invoke($"The file from another device could not be marked as from the internet: {ex.GetType().Name}.");
        }
    }
}
