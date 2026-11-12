using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R24: on each connection the speed graph takes the Host's last hour, so it shows that hour although the window
/// was closed for some of it (owner, 2026-11-08: "Keep the hour"). The window's own one-second samples carry on from there.
/// </summary>
public sealed partial class MainWindow
{
    private async Task LoadSpeedHistoryAsync()
    {
        try
        {
            var reply = await _connection.GetSpeedHistoryAsync();
            // A Host from before UI.R24 answers with an error; the graph then starts from now, as it did.
            if (reply.Type != MessageTypes.SpeedHistoryResult) return;
            var history = reply.GetPayload<SpeedHistoryDto>();
            if (history is null) return;
            // T8: a Host before the upload history sends none; the upload line then starts from now.
            SpeedGraph.LoadHistory(history.Start, history.IntervalSeconds, history.BytesPerSecond, history.UploadBytesPerSecond);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Speed history not loaded: {ex.Message}");
        }
    }
}
