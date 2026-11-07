using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The Extraction page of Options, from <c>archive.settings</c>. Unlike a provider's key, these follow
/// the window's OK, Cancel and Apply like every other setting. The word on whether 7-Zip is there
/// comes only from the Host, because the Host is what runs it.
/// </summary>
internal sealed class ExtractionOptionsPage
{
    private readonly RemoteClient _client;
    private readonly SettingsWindow _window;
    private readonly CancellationTokenSource _closing = new();
    private readonly CheckBox _autoExtract = new() { Content = "Extract archives automatically after they finish downloading" };
    private readonly StatusLine _toolStatus = new("Extractor status");
    private readonly RadioButtons _destination = new() { Header = "Extract to", ItemsSource = ArchiveForm.DestinationLabels };
    private readonly UIElement _folderRow;
    private readonly TextBox _folder;
    private readonly Button _browseFolder;
    private readonly CheckBox _preserveFolders = new() { Content = "Keep the folders stored in the archive" };
    private readonly RadioButtons _conflict = new() { Header = "If a file with the same name is already there", ItemsSource = ArchiveForm.ConflictLabels };
    private readonly RadioButtons _archives = new() { Header = "After a successful extraction", ItemsSource = new[] { "Keep the archive files", "Delete the archive files" } };
    private readonly StatusLine _loadStatus = new("Extraction settings");
    private readonly Button _tryAgain = new() { Content = "Try again", Visibility = Visibility.Collapsed };
    private readonly List<Control> _fields;

    private ArchiveSettingsDto? _saved;
    private ArchiveToolStatusDto? _tool;

    public ExtractionOptionsPage(RemoteClient client, SettingsWindow window)
    {
        _client = client;
        _window = window;

        AutomationProperties.SetName(_autoExtract, "Extract archives automatically after they finish downloading");
        AutomationProperties.SetName(_destination, "Extract to");
        AutomationProperties.SetName(_preserveFolders, "Keep the folders stored in the archive");
        AutomationProperties.SetName(_conflict, "If a file with the same name is already there");
        AutomationProperties.SetName(_archives, "After a successful extraction");
        AutomationProperties.SetName(_tryAgain, "Try reading the extraction settings again");

        var (folderRow, folder) = window.FolderField("Extraction folder", null);
        _folderRow = folderRow;
        _folder = folder;
        _browseFolder = ((Grid)folderRow).Children.OfType<Button>().First();
        ((FrameworkElement)folderRow).Margin = new Thickness(28, 0, 0, 0);

        _fields = [_autoExtract, _destination, _preserveFolders, _conflict, _archives];
        SetFieldsEnabled(false);

        _destination.SelectionChanged += (_, _) => UpdateFolderRow();
        _tryAgain.Click += (_, _) => _ = LoadAsync();

        Controls =
        [
            _loadStatus.Text,
            _tryAgain,
            SettingsWindow.Section("When a download finishes"),
            _autoExtract,
            SettingsWindow.Hint("Applies to archives that finish after this is turned on. A multi-part archive waits for all its parts. Any finished archive can also be extracted by hand."),
            SettingsWindow.Section("Extractor"),
            SettingsWindow.Hint("Inbound extracts with 7-Zip, which comes with it: RAR, 7z, zip and the other formats 7-Zip reads, with passwords and multi-part sets. 7-Zip is free software under the GNU LGPL, from www.7-zip.org."),
            _toolStatus.Text,
            SettingsWindow.Section("Where files go"),
            _destination,
            _folderRow,
            _preserveFolders,
            SettingsWindow.Hint("Turned off, every file goes straight into the destination."),
            _conflict,
            SettingsWindow.Section("Archive files"),
            _archives,
            SettingsWindow.Hint("Archives are deleted only after the extraction has succeeded and its files have passed verification. If it fails or is cancelled, they are kept."),
        ];

        _ = LoadAsync();
    }

    public UIElement[] Controls { get; }

    /// <summary>What is wrong with the page before anything is saved, or null.</summary>
    public string? Validate()
    {
        if (_saved is null) return null;
        ArchiveForm.Changes(_saved, Values(), out var error);
        return error;
    }

    /// <summary>Sends what changed. Returns what the user should see, or null when all is well.</summary>
    public async Task<string?> SaveAsync()
    {
        if (_saved is null) return null;
        var update = ArchiveForm.Changes(_saved, Values(), out var error);
        if (error is not null) return error;
        if (update is null) return null;

        ArchiveSettingsResult result;
        try
        {
            result = await _client.SetArchiveSettingsAsync(update, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (!result.Ok) return RemoteText.Failure(result.Error);
        Show(result);
        return null;
    }

    public void Close() => _closing.Cancel();

    private async Task LoadAsync()
    {
        _tryAgain.Visibility = Visibility.Collapsed;
        _loadStatus.Show("Reading the extraction settings…");
        ArchiveSettingsResult result;
        try
        {
            result = await _client.GetArchiveSettingsAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!result.Ok)
        {
            // Nothing on this page can be trusted, so nothing on it can be changed or saved.
            _loadStatus.Show($"Couldn't read the extraction settings. {RemoteText.Failure(result.Error)}", isProblem: true);
            _tryAgain.Visibility = Visibility.Visible;
            return;
        }

        _loadStatus.Show(string.Empty);
        Show(result);
    }

    private void Show(ArchiveSettingsResult result)
    {
        _saved = result.Settings;
        _tool = result.Tool;
        var values = ArchiveForm.FromSettings(result.Settings);
        _autoExtract.IsChecked = values.AutoExtract;
        _destination.SelectedIndex = values.DestinationIndex;
        _folder.Text = values.Folder;
        _preserveFolders.IsChecked = values.PreserveFolders;
        _conflict.SelectedIndex = values.ConflictIndex;
        _archives.SelectedIndex = values.DeleteArchives ? 1 : 0;
        SetFieldsEnabled(true);
        UpdateFolderRow();
        ShowToolStatus();
    }

    private ArchiveFormValues Values() => new(
        _autoExtract.IsChecked == true,
        _destination.SelectedIndex,
        _folder.Text,
        _preserveFolders.IsChecked == true,
        _conflict.SelectedIndex,
        _archives.SelectedIndex == 1);

    private void SetFieldsEnabled(bool enabled)
    {
        foreach (var field in _fields) field.IsEnabled = enabled;
        if (!enabled) _folder.IsEnabled = _browseFolder.IsEnabled = false;
    }

    private void UpdateFolderRow()
    {
        var used = _saved is not null && _destination.SelectedIndex == ArchiveForm.DestinationFolderIndex;
        _folder.IsEnabled = _browseFolder.IsEnabled = used;
    }

    private void ShowToolStatus()
    {
        if (_tool is null) return;
        var (text, isProblem) = RemoteText.ToolStatus(_tool);
        _toolStatus.Show(text, isProblem);
    }
}
