using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using WinRT.Interop;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The Extraction page of Options, from <c>archive.settings</c>. Unlike a provider's key, these follow
/// the window's OK, Cancel and Apply like every other setting. The word on whether UnRAR is there
/// comes only from the Host, because the Host is what would run it.
/// </summary>
internal sealed class ExtractionOptionsPage
{
    private readonly RemoteClient _client;
    private readonly SettingsWindow _window;
    private readonly CancellationTokenSource _closing = new();
    private readonly CheckBox _autoExtract = new() { Content = "Extract RAR archives automatically after they finish downloading" };
    private readonly TextBox _unrarPath = new() { Header = "UnRAR program", PlaceholderText = "Empty: look for UnRAR on this computer" };
    private readonly Button _browseUnrar = new() { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom, MinWidth = 92 };
    private readonly StatusLine _toolStatus = new("UnRAR status");
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

        AutomationProperties.SetName(_autoExtract, "Extract RAR archives automatically after they finish downloading");
        AutomationProperties.SetName(_unrarPath, "UnRAR program path");
        AutomationProperties.SetName(_browseUnrar, "Browse for the UnRAR program");
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

        var unrarRow = new Grid { ColumnSpacing = 8 };
        unrarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        unrarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        unrarRow.Children.Add(_unrarPath);
        Grid.SetColumn(_browseUnrar, 1);
        unrarRow.Children.Add(_browseUnrar);

        _fields = [_autoExtract, _unrarPath, _browseUnrar, _destination, _preserveFolders, _conflict, _archives];
        SetFieldsEnabled(false);

        _unrarPath.TextChanged += (_, _) => ShowToolStatus();
        _browseUnrar.Click += (_, _) => _ = BrowseUnrarAsync();
        _destination.SelectionChanged += (_, _) => UpdateFolderRow();
        _tryAgain.Click += (_, _) => _ = LoadAsync();

        Controls =
        [
            _loadStatus.Text,
            _tryAgain,
            SettingsWindow.Section("When a download finishes"),
            _autoExtract,
            SettingsWindow.Hint("Applies to archives that finish after this is turned on. A multi-part archive waits for all its parts. Any finished archive can also be extracted by hand."),
            SettingsWindow.Section("UnRAR"),
            SettingsWindow.Hint("Extraction runs UnRAR, which comes with WinRAR. This app doesn't include it."),
            unrarRow,
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

        // It is saved. A path with nothing there deserves a look before the window closes, once.
        return update.UnrarPath is not null && !result.Tool.ConfiguredPathFound
            ? "Saved, but there is no UnRAR at that path. Choose it again, or select OK to leave it as it is."
            : null;
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
        _unrarPath.Text = values.UnrarPath;
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
        _unrarPath.Text,
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
        var (text, isProblem) = RemoteText.ToolStatus(_tool, _unrarPath.Text);
        _toolStatus.Show(text, isProblem);
    }

    private async Task BrowseUnrarAsync()
    {
        try
        {
            if (await Pickers.PickFilesAsync(WindowNative.GetWindowHandle(_window), [".exe"], multiple: false, PickerLocationId.ComputerFolder) is [var file]) _unrarPath.Text = file;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _toolStatus.Show("The file picker could not be opened. Type the full path of UnRAR.exe instead.", isProblem: true);
        }
    }
}
