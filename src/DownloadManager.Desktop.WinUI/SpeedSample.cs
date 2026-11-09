namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.B20: the one rule both speed graphs sample by, once a second on the window's own clock. A running download
/// whose last update is recent counts at that update's rate until the next one arrives; a download that has gone quiet
/// (stalled, connecting, waiting to retry) or stopped counts as 0. Only a lost Host connection is a gap.
/// </summary>
internal static class SpeedSample
{
    /// <summary>How long an update's rate is held before the download counts as transferring nothing.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);

    public static bool IsActive(string state) => state is "connecting" or "downloading" or "verifying" or "retryWaiting";

    /// <summary>One download's figure for the graph at <paramref name="now"/>.</summary>
    public static double JobRate(string state, double? bytesPerSecond, DateTimeOffset updatedAt, DateTimeOffset now) =>
        IsActive(state) && now - updatedAt <= Fresh && bytesPerSecond is { } rate && double.IsFinite(rate) ? Math.Max(0, rate) : 0;

    /// <summary>The graph's sample: null (a gap) while not connected, otherwise the downloads' figures added up.</summary>
    public static double? Total(bool connected, IEnumerable<double> rates) => connected ? rates.Sum() : null;
}
