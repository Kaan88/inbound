using System.Globalization;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Shows update progress without owning or cancelling the Host operation.</summary>
public sealed partial class UpdateWindow : Window
{
    private readonly HostConnection _connection = new();
    private string _stage = "checking";
    private bool _closed;

    public UpdateWindow()
    {
        InitializeComponent();
        AppTheme.Track(Root);
        // UI.R14: sized for the display's scaling (it was clipped at 150%), and in the middle of the main window.
        WindowLayout.ResizeForDpi(this, 460, 250);
        WindowLayout.CenterOnMainWindow(this);
        var icon = WindowLayout.AppIconPath;
        if (File.Exists(icon)) AppWindow.SetIcon(WindowLayout.IconArgument(icon));
        Title = "Update — Inbound Download Manager";
        WindowLayout.ApplyChrome(this, Root);

        _connection.UpdateProgressChanged += progress => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) ApplyProgress(progress);
        });
        _connection.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed && snapshot.UpdateProgress is not null)
                ApplyProgress(snapshot.UpdateProgress);
        });
        _connection.ConnectionChanged += (connected, error) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (!connected)
            {
                if (string.Equals(_stage, "installing", StringComparison.OrdinalIgnoreCase))
                {
                    StatusText.Text = "Restarting to finish installing the update…";
                    DetailText.Text = "The update helper is replacing the application files.";
                    UpdateProgress.IsIndeterminate = true;
                    UpdateProgress.Visibility = Visibility.Visible;
                }
                else
                {
                    StatusText.Text = "Waiting for Inbound to reconnect…";
                    DetailText.Text = error ?? string.Empty;
                }
                return;
            }

            if (!string.Equals(_stage, "installing", StringComparison.OrdinalIgnoreCase))
                DetailText.Text = string.Empty;
        });
        Closed += async (_, _) =>
        {
            _closed = true;
            await _connection.DisposeAsync();
        };

        ApplyProgress(new UpdateProgressDto { Stage = "checking" });
        _connection.Start();
    }

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void ApplyProgress(UpdateProgressDto progress)
    {
        _stage = progress.Stage ?? string.Empty;
        var stage = _stage.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(progress.Version))
            VersionText.Text = $"Version {progress.Version}";

        UpdateProgress.Visibility = Visibility.Visible;
        DetailText.Text = progress.Error ?? string.Empty;
        PercentText.Text = string.Empty;

        switch (stage)
        {
            case "checking":
                StatusText.Text = "Checking for updates…";
                SetIndeterminate("Contacting the update service…");
                break;
            case "downloading":
                StatusText.Text = "Downloading update…";
                ApplyDownloadProgress(progress);
                break;
            case "verifying":
                StatusText.Text = "Verifying downloaded files…";
                SetIndeterminate("Checking the signed package and file hash…");
                break;
            case "staging":
                StatusText.Text = "Preparing the update…";
                SetIndeterminate("Extracting the update package…");
                break;
            case "installing":
                StatusText.Text = "Installing update…";
                SetIndeterminate("The application will restart to finish installation.");
                break;
            case "failed":
                StatusText.Text = "Update failed";
                StopProgress("The current application remains available.");
                break;
            case "postponed":
                StatusText.Text = "Update postponed";
                StopProgress("The update can be tried again later.");
                break;
            case "uptodate":
                StatusText.Text = "You're up to date";
                StopProgress("No update is available.");
                break;
            default:
                StatusText.Text = string.IsNullOrWhiteSpace(_stage)
                    ? "Waiting for update status…"
                    : _stage;
                SetIndeterminate("Waiting for the update service…");
                break;
        }
    }

    private void ApplyDownloadProgress(UpdateProgressDto progress)
    {
        var received = Math.Max(0, progress.ReceivedBytes);
        var total = progress.TotalBytes;
        if (total is > 0)
        {
            var safeTotal = total.Value;
            received = Math.Min(received, safeTotal);
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = received * 100d / safeTotal;
            BytesText.Text = $"{FormatBytes(received)} of {FormatBytes(safeTotal)}";
            PercentText.Text = UpdateProgress.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%";
        }
        else
        {
            SetIndeterminate($"{FormatBytes(received)} downloaded");
        }
    }

    private void SetIndeterminate(string details)
    {
        UpdateProgress.IsIndeterminate = true;
        UpdateProgress.Value = 0;
        BytesText.Text = details;
    }

    private void StopProgress(string fallbackDetail)
    {
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Collapsed;
        BytesText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(DetailText.Text))
            DetailText.Text = fallbackDetail;
    }

    private static string FormatBytes(long bytes) => UiFormat.Bytes(bytes);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
