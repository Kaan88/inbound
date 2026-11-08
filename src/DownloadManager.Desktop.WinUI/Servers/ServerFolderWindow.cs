using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Chooses a folder on another server (design D9, section 5): its <c>folders.browse</c> and <c>folders.create</c>, one
/// <c>POST /api/ipc</c> each, so a folder box never offers a folder of this PC for a download saved there. A window of
/// its own rather than a dialog, because it is opened from dialogs too.
/// </summary>
internal sealed class ServerFolderWindow : Window
{
    private readonly ServerTarget _target;
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBox _path = new() { Header = "Folder", PlaceholderText = "Choose one of the server's folders below" };
    private readonly ListView _folders = new() { SelectionMode = ListViewSelectionMode.Single, MinHeight = 240 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly TextBlock _space = new() { FontSize = 12, Opacity = 0.72 };
    private readonly Button _up = new() { Content = "Up one level" };
    private readonly Button _newFolder = new() { Content = "New folder…" };
    private readonly Button _choose = new() { Content = "Choose this folder" };
    private FolderBrowseResult? _shown;
    private int _version;

    private ServerFolderWindow(ServerTarget target)
    {
        _target = target;
        Title = $"Choose a folder on {target.Name}";
        WindowLayout.ResizeForDpi(this, 560, 520);
        var root = new Grid { Padding = new Thickness(16), RowSpacing = 10 };
        AppTheme.Track(root);
        for (var i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = $"These are the folders on {target.Name} that this app may save to.",
            TextWrapping = TextWrapping.Wrap,
        });
        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(_path);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_up);
        buttons.Children.Add(_newFolder);
        top.Children.Add(buttons);
        Grid.SetRow(top, 1);
        root.Children.Add(top);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_folders, "Folders");
        Grid.SetRow(_folders, 2);
        root.Children.Add(_folders);
        var status = new StackPanel { Spacing = 4 };
        status.Children.Add(_space);
        status.Children.Add(_message);
        Grid.SetRow(status, 3);
        root.Children.Add(status);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        _choose.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var cancel = new Button { Content = "Cancel" };
        footer.Children.Add(_choose);
        footer.Children.Add(cancel);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        Content = root;
        WindowLayout.ApplyChrome(this, root);
        WindowLayout.CenterOnMainWindow(this);

        _folders.DisplayMemberPath = nameof(FolderEntryDto.Name);
        _folders.IsItemClickEnabled = true;
        _folders.ItemClick += (_, e) => { if (e.ClickedItem is FolderEntryDto entry) _ = BrowseAsync(entry.Path); };
        _up.Click += (_, _) => _ = BrowseAsync(_shown?.Parent);
        _newFolder.Click += (_, _) => _ = CreateFolderAsync();
        _path.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            _ = BrowseAsync(_path.Text.Trim());
        };
        _choose.Click += (_, _) =>
        {
            var chosen = _path.Text.Trim();
            if (chosen.Length == 0)
            {
                Say("Open a folder first.");
                return;
            }

            _result.TrySetResult(chosen);
            Close();
        };
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>The chosen folder's path on the server, or null when cancelled.</summary>
    public static Task<string?> PickAsync(ServerTarget target, string? start = null)
    {
        var window = new ServerFolderWindow(target);
        window.Activate();
        _ = window.BrowseAsync(string.IsNullOrWhiteSpace(start) ? null : start.Trim(), fallBackToRoots: true);
        return window._result.Task;
    }

    private async Task BrowseAsync(string? path, bool fallBackToRoots = false)
    {
        var result = await AskAsync(MessageTypes.FoldersBrowse, new FolderBrowseRequest { Path = path });
        if (result is { Ok: false } && fallBackToRoots && path is not null) result = await AskAsync(MessageTypes.FoldersBrowse, new FolderBrowseRequest());
        Show(result);
    }

    private async Task CreateFolderAsync()
    {
        if (_shown?.Path is not { } parent)
        {
            Say("Open one of the folders first; a new folder is made inside it.");
            return;
        }

        var name = new TextBox { Header = "Name of the new folder", MaxLength = 255 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "New folder",
            Content = name,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || name.Text.Trim().Length == 0) return;
        Show(await AskAsync(MessageTypes.FoldersCreate, new FolderCreateRequest { Parent = parent, Name = name.Text.Trim() }));
    }

    private async Task<FolderBrowseResult?> AskAsync<T>(string type, T request)
    {
        var mine = ++_version;
        _message.Visibility = Visibility.Collapsed;
        try
        {
            var reply = await ServerConnector.SendOnceAsync(_target, IpcMessage.Create(type, request), TimeSpan.FromSeconds(20));
            if (mine != _version) return null;
            if (reply.Type != MessageTypes.FoldersBrowseResult)
                return new FolderBrowseResult { Error = $"{_target.Name} can't list its folders for this app." };
            return reply.GetPayload<FolderBrowseResult>();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (mine != _version) return null;
            return new FolderBrowseResult { Error = ex is RemoteServerException ? ex.Message : $"Couldn't reach {_target.Name}." };
        }
    }

    private void Show(FolderBrowseResult? result)
    {
        if (result is null) return;
        if (!result.Ok)
        {
            Say(result.Error ?? "That folder can't be opened.");
            return;
        }

        _shown = result;
        _path.Text = result.Path ?? string.Empty;
        // The roots are listed with their whole paths, a folder's contents by name.
        _folders.ItemsSource = result.Path is null
            ? result.Roots.Select(root => new FolderEntryDto { Name = root, Path = root }).ToList()
            : result.Folders;
        _up.IsEnabled = result.Path is not null;
        _newFolder.IsEnabled = result.Path is not null;
        _space.Text = result.FreeBytes is { } free
            ? result.TotalBytes is { } total ? $"{FolderChooser.Size(free)} free of {FolderChooser.Size(total)}" : $"{FolderChooser.Size(free)} free"
            : string.Empty;
        if (result.Truncated) Say("Only the first 2,000 folders are listed. Type a path to open one further down.");
    }

    private void Say(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
    }
}
