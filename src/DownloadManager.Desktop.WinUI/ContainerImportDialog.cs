using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What came of the import dialog.</summary>
/// <param name="Outcomes">The Host answered an add with how each entry went. Null when nothing was asked of it, or the answer never came.</param>
/// <param name="Folder">The destination asked for, when one was typed or kept.</param>
/// <param name="Stopped">Closed while adding, so some entries may have been added and the rest weren't.</param>
/// <param name="CouldNotOpen">Another dialog is open over this window, and nothing was asked of the Host.</param>
internal sealed record ContainerImportAnswer(ContainerOutcomes? Outcomes, string? Folder, bool Stopped, bool CouldNotOpen = false);

/// <summary>
/// Import Link Container (U-WL-22: once "Import JD Container"). It opens straight away and reads the file on a connection of its own, and closing it
/// closes that connection, which is what cancels the call. The dialog only ever holds what the Host says:
/// names, words, and that a password is stated. It never looks at the file: the path goes to the Host, which
/// refuses a network path before anything asks about it, and that refusal is what gets shown.
/// </summary>
internal sealed class ContainerImportDialog
{
    private const int MostNotes = 5;

    private readonly RemoteClient _remote;
    private readonly string _provider;
    private readonly string _path;
    private readonly string _fileName;
    private readonly int _queueId;
    private readonly FrameworkElement _themeRoot;
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly StatusLine _status = new("Import status");
    private readonly StatusLine _accountProblem = new("Account problem");
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly StackPanel _listPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly ContainerRowList _rows = new() { Height = 260, ListName = "Packages and links in this container" };
    private readonly Button _selectAll = new() { Content = "Select all", IsEnabled = false };
    private readonly Button _selectNone = new() { Content = "Select none", IsEnabled = false };
    private readonly StatusLine _summary = new("Links selected");
    private readonly StackPanel _addPanel = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    private readonly StackPanel _sendPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly Button _send = new();
    private readonly Button _stopSending = new() { Content = "Stop waiting", Visibility = Visibility.Collapsed };
    private readonly ProgressRing _sendBusy = new() { IsActive = false, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly StatusLine _sendStatus = new("JDownloader status");
    private readonly CancellationTokenSource _closing = new();
    private readonly UIElement _destinationRow;
    private readonly TextBox _destination;
    private readonly StatusLine _destinationError;
    private ContentDialog? _dialog;
    private ContainerPicks? _picks;
    private CancellationTokenSource? _sendWait;
    private ContainerOutcomes? _outcomes;
    private Task? _adding;
    private string? _folder;
    private bool _reading;
    private bool _isAdding;
    private bool _isSending;
    private bool _stopped;
    private bool _mustReadAgain;

    public ContainerImportDialog(RemoteClient remote, SettingsDto settings, string provider, string path, FrameworkElement themeRoot, IntPtr windowHandle)
    {
        _remote = remote;
        _provider = provider;
        _path = path;
        _themeRoot = themeRoot;
        _fileName = Path.GetFileName(path);
        _queueId = settings.DefaultQueueId;

        // The same memory Add URL and the My provider window use.
        var initial = string.IsNullOrWhiteSpace(settings.LastDestinationDirectory) ? settings.DefaultDownloadDirectory : settings.LastDestinationDirectory;
        (_destinationRow, _destination, _destinationError) = AddLinksDialog.DestinationRow(initial, windowHandle);
    }

    public async Task<ContainerImportAnswer> ShowAsync(XamlRoot root, ElementTheme theme)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = "Import Link Container",
            Content = new ScrollViewer
            {
                Padding = InboundTheme.DialogScrollPadding,
                Content = BuildContent(),
                MaxHeight = Math.Max(240, Math.Min(620, _themeRoot.ActualHeight - 200)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = false,

            // A default button that starts disabled doesn't pick up the accent by itself.
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        _dialog = dialog;
        dialog.PrimaryButtonClick += OnPrimary;
        UpdateButtons();

        // Refused at once, without showing, when another dialog is open over this window (UI.B8); nothing has been
        // asked yet then, so nothing is read either.
        var shown = DialogGuard.ShowAsync(dialog);
        if (shown.IsCompleted && shown.Result is null)
        {
            _closing.Cancel();
            return new ContainerImportAnswer(null, null, false, CouldNotOpen: true);
        }

        _ = ReadAsync();
        if (await shown is null)
        {
            // It failed to open after all.
            _closing.Cancel();
            return new ContainerImportAnswer(null, null, false, CouldNotOpen: true);
        }

        // Closing ends whatever was running. An add that was on its way says what it got to.
        _closing.Cancel();
        if (_adding is { } adding) await adding;
        return new ContainerImportAnswer(_outcomes, _folder, _stopped);
    }

    private StackPanel BuildContent()
    {
        AutomationProperties.SetName(_busy, "Working on this container");
        AutomationProperties.SetName(_selectAll, "Select all links that can be added");
        AutomationProperties.SetName(_selectNone, "Select no links");
        AutomationProperties.SetName(_notes, "About this file");
        _selectAll.Click += (_, _) =>
        {
            _picks?.SelectAll();
            Refresh();
        };
        _selectNone.Click += (_, _) =>
        {
            _picks?.SelectNone();
            Refresh();
        };
        _rows.RowToggled += (index, chosen) =>
        {
            _picks?.Toggle(index, chosen);
            Refresh();
        };
        _notes.Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _themeRoot);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _selectAll, _selectNone, _summary.Text } };
        _summary.Text.VerticalAlignment = VerticalAlignment.Center;
        _summary.Text.FontSize = 12;
        _listPanel.Children.Add(buttons);
        _listPanel.Children.Add(_rows);

        _addPanel.Children.Add(_destinationRow);
        _addPanel.Children.Add(_destinationError.Text);

        BuildSendPanel();

        _status.Text.MaxWidth = 610;
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _busy, _status.Text } };
        return new StackPanel
        {
            Spacing = 10,
            MinWidth = 640,
            Padding = new Thickness(0, 0, 14, 0),
            Children = { statusRow, _accountProblem.Text, _notes, _listPanel, _addPanel, _sendPanel },
        };
    }

    /// <summary>Never happens by itself: this button is the only way there, and it says what it does.</summary>
    private void BuildSendPanel()
    {
        const string Words = "Open in JDownloader and wait for an answer there, for up to five minutes";
        _send.Content = new TextBlock { Text = Words, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(_send, Words);
        AutomationProperties.SetName(_sendBusy, "Waiting for JDownloader's answer");
        AutomationProperties.SetName(_stopSending, "Stop waiting for JDownloader");
        var hint = new TextBlock
        {
            Text = "This is a separate choice. JDownloader has to be running on this computer, and it asks you to confirm there. The links stay in JDownloader: they don't come back here.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _themeRoot),
        };
        AutomationProperties.SetName(hint, hint.Text);
        _send.Click += (_, _) => _ = SendAsync();

        // A separate choice, so the dialog's first focus can't land on it and take a stray Enter. Tab and a click still reach it.
        _send.GettingFocus += (_, args) =>
        {
            if (args.FocusState == FocusState.Programmatic && FindCloseButton() is { } close) args.TrySetNewFocusedElement(close);
        };
        _stopSending.Click += (_, _) => _sendWait?.Cancel();
        _sendStatus.Text.MaxWidth = 610;
        _sendPanel.Children.Add(hint);
        _sendPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _send, _stopSending } });
        _sendPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _sendBusy, _sendStatus.Text } });
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Kept open: it closes by itself once the Host has answered, and until then it says how far it got.
        args.Cancel = true;
        if (_reading || _isAdding) return;
        if (_mustReadAgain)
        {
            _ = ReadAsync();
            return;
        }

        if (_picks is not { CanAdd: true, PreviewId: { } previewId } picks) return;
        if (picks.IsExpired(DateTimeOffset.UtcNow))
        {
            _mustReadAgain = true;
            _status.Say("The links read from this file have been forgotten, as they are after a while. Read the file again to add them.", isProblem: true);
            UpdateButtons();
            return;
        }

        var folder = _destination.Text.Trim();
        if (folder.Length > 0 && !AddLinksDialog.IsFullPath(folder))
        {
            _destinationError.Show(AddLinksDialog.FullPathWords, isProblem: true);
            _destination.Focus(FocusState.Programmatic);
            return;
        }

        _adding = AddAsync(previewId, picks, folder.Length > 0 ? folder : null);
    }

    private async Task ReadAsync()
    {
        _reading = true;
        _mustReadAgain = false;
        _picks = null;
        _rows.SetRows([]);
        _listPanel.Visibility = _addPanel.Visibility = _sendPanel.Visibility = Visibility.Collapsed;
        _accountProblem.Show(string.Empty);
        ShowNotes([]);
        ShowBusy(true);
        _status.Show($"Reading {_fileName}…");
        UpdateButtons();
        try
        {
            var reply = await _remote.ReadContainerAsync(_path, _closing.Token);
            if (_closing.IsCancellationRequested) return;
            var preview = reply.Result;
            var view = RemoteText.ContainerRead(preview, _fileName, canReadAgain: reply.CanReadAgain);
            _status.Show(view.Status, view.IsProblem);
            _accountProblem.Show(view.AccountProblem ?? string.Empty, isProblem: true);
            ShowNotes(view.Notes);
            _mustReadAgain = view.CanReadAgain;
            _sendPanel.Visibility = view.CanSendToJd ? Visibility.Visible : Visibility.Collapsed;
            if (view.ShowList)
            {
                _picks = new ContainerPicks(preview, _provider);
                _rows.SetRows(_picks.Views());
                _listPanel.Visibility = Visibility.Visible;
                _addPanel.Visibility = preview.PreviewId is null ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog closed first, and there is nothing left to show it in.
            return;
        }
        finally
        {
            _reading = false;
        }

        ShowBusy(false);
        Refresh();
    }

    private async Task AddAsync(Guid previewId, ContainerPicks picks, string? folder)
    {
        _isAdding = true;
        _folder = folder;
        _rows.IsLocked = true;
        _selectAll.IsEnabled = _selectNone.IsEnabled = _destination.IsEnabled = false;
        var ids = picks.ChosenEntryIds;
        ShowBusy(true);
        _status.Show(ids.Count == 1 ? "Adding 1 link…" : $"Adding {ids.Count} links…");
        UpdateButtons();
        try
        {
            var result = await _remote.AddFromContainerAsync(AddRequests.ForContainer(previewId, ids, folder, _queueId), _closing.Token);

            // Every entry the Host says anything about has its line, whichever way it went.
            if (result.Outcomes.Count > 0)
            {
                _outcomes = new ContainerOutcomes(result, picks.NameOf);
                _isAdding = false;
                if (!_closing.IsCancellationRequested) _dialog?.Hide();
                return;
            }

            // An expired preview, or a lost connection. Reading the file again is safe either way: the Host won't add an active job twice.
            _status.Say(RemoteText.Failure(result.Error), isProblem: true);
            _mustReadAgain = true;
        }
        catch (OperationCanceledException)
        {
            _stopped = true;
            return;
        }
        finally
        {
            _isAdding = false;
        }

        _rows.IsLocked = false;
        _destination.IsEnabled = true;
        ShowBusy(false);
        Refresh();
    }

    private async Task SendAsync()
    {
        if (_isSending) return;
        _isSending = true;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _sendWait = wait;
        _send.IsEnabled = false;
        _stopSending.Visibility = Visibility.Visible;
        _sendBusy.IsActive = true;
        _sendBusy.Visibility = Visibility.Visible;
        _sendStatus.Show("Waiting for JDownloader's answer. It asks you there, for up to five minutes.");
        UpdateButtons();
        var again = true;
        try
        {
            var view = RemoteText.ContainerSend(await _remote.SendContainerToJdAsync(_path, wait.Token));
            if (_closing.IsCancellationRequested) return;
            again = view.CanSendAgain;
            _sendStatus.Say(view.Text, view.IsProblem);
        }
        catch (OperationCanceledException)
        {
            if (_closing.IsCancellationRequested) return;
            _sendStatus.Say("Stopped waiting. JDownloader may still be asking, so look there before sending this again.");
        }
        finally
        {
            _isSending = false;
            _sendWait = null;
        }

        _send.IsEnabled = again;
        _stopSending.Visibility = Visibility.Collapsed;
        _sendBusy.IsActive = false;
        _sendBusy.Visibility = Visibility.Collapsed;
        UpdateButtons();
    }

    /// <summary>An open dialog's buttons live in its popup, not under the dialog itself.</summary>
    private Button? FindCloseButton()
    {
        if (_dialog?.XamlRoot is not { } root) return null;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is { } child && Find(child) is { } found) return found;
        }

        return null;

        static Button? Find(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button { Name: "CloseButton" } button) return button;
                if (Find(child) is { } found) return found;
            }

            return null;
        }
    }

    private void ShowBusy(bool busy)
    {
        _busy.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowNotes(IReadOnlyList<string> notes)
    {
        var lines = notes.Take(MostNotes).ToList();
        if (notes.Count > MostNotes) lines.Add($"and {notes.Count - MostNotes} more");
        _notes.Text = string.Join(Environment.NewLine, lines);
        _notes.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_notes, lines.Count > 0 ? $"About this file: {string.Join(". ", lines)}" : "About this file");
    }

    private void Refresh()
    {
        if (_picks is { } picks)
        {
            _rows.Apply(picks.Views());
            _summary.Show(picks.Summary());
            var any = picks.AddableCount > 0;
            _selectAll.IsEnabled = any && picks.ChosenCount < picks.AddableCount;
            _selectNone.IsEnabled = any && picks.ChosenCount > 0;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (_dialog is not { } dialog) return;
        var canAdd = !_reading && !_isAdding && !_mustReadAgain && _picks is { CanAdd: true };
        var canRead = !_reading && !_isAdding && _mustReadAgain;

        // No button at all when there is nothing it could ever do here.
        dialog.PrimaryButtonText = _mustReadAgain ? "Read the file again" : _picks is { PreviewId: not null } ? "Add" : string.Empty;
        dialog.IsPrimaryButtonEnabled = canAdd || canRead;

        // Enter takes the default button, so it only means the action while the action can run. While something
        // is under way the close button stops it, which takes a deliberate press or Esc rather than a second Enter.
        dialog.DefaultButton = _isAdding || _isSending
            ? ContentDialogButton.None
            : canAdd || canRead ? ContentDialogButton.Primary : ContentDialogButton.Close;
        dialog.CloseButtonText = _isAdding ? "Stop adding" : _isSending ? "Stop and close" : "Cancel";
    }
}
