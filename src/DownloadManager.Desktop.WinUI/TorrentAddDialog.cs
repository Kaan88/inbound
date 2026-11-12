using DownloadManager.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Add Magnet, Import Torrent File and the torrents found in pasted or dropped text (T1 step 5, mockup sections 4 to 6).
/// Each line is asked about (torrent.inspect) as soon as it is listed, at most <see cref="MostAtOnce"/> at a time, and
/// can be added once it is read. With an account that takes torrents each line also chooses This PC or that account
/// (one is picked from the start, T9 W1), with that account's cache answer as a badge (W3). Nothing is added here: the
/// choice goes back to the main window, which sends every line, the account's too, without another dialog (W2). The
/// lines' rules are in <see cref="TorrentAddLines"/>.
/// </summary>
internal sealed class TorrentAddDialog
{
    private const int MostAtOnce = 4;
    private const double RowTextWidth = 470;
    private static readonly TimeSpan PasteDelay = TimeSpan.FromMilliseconds(400);

    private readonly RemoteClient _remote;
    private readonly SettingsDto _settings;
    private readonly string? _providerName;
    private readonly FrameworkElement _themeRoot;
    private readonly IntPtr _windowHandle;
    private readonly TorrentAddLines _lines;
    private readonly StackPanel _list = new() { Spacing = 8, Padding = new Thickness(8, 6, 8, 6) };
    private readonly Dictionary<TorrentAddLine, LineRow> _rows = [];
    private readonly Dictionary<TorrentAddLine, CancellationTokenSource> _asking = [];
    private readonly StatusLine _status = new("Torrents status");
    private readonly CheckBox _selectAll = new() { Content = "Select all", Visibility = Visibility.Collapsed };
    private readonly TextBlock _magnetNote = InboundUi.Secondary("A magnet asks which files to download once its file list arrives.");
    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim _turns = new(MostAtOnce);
    private readonly Queue<(TorrentAddLine Line, string Query)> _cacheChecks = new();
    private bool _checkingCache;
    private Border? _listCard;
    private ContentDialog? _dialog;
    private TextBox? _paste;
    private DispatcherOneShot? _pasteTimer;
    private string _pastedText = string.Empty;
    private bool _closed;
    private Guid? _showJobId;
    private readonly bool _withControls;
    private readonly Func<string, Guid?>? _jobFor;

    /// <summary>T3: the destination the lines were last asked about (a full path), or null for each category folder.</summary>
    private string? _spaceFolder;
    private DispatcherOneShot? _spaceTimer;
    private TextBox? _destination;

    /// <param name="providerName">The account's display name when an account that takes torrents exists, else null: then
    /// there is no route choice and every line is This PC.</param>
    /// <param name="withControls">T3: the Host takes file priorities and says the free space at the destination.</param>
    /// <param name="jobFor">T10: the Host's finished .torrent download a line's path is the file of, or null. Such a line is
    /// read by the Host from that download (FromJobId), so its path need not be on this PC.</param>
    public TorrentAddDialog(RemoteClient remote, SettingsDto settings, string? providerName, FrameworkElement themeRoot, IntPtr hwnd,
        bool withControls = false, Func<string, Guid?>? jobFor = null)
    {
        _withControls = withControls;
        _jobFor = jobFor;
        _remote = remote;
        _settings = settings;
        _providerName = string.IsNullOrWhiteSpace(providerName) ? null : providerName.Trim();
        _themeRoot = themeRoot;
        _windowHandle = hwnd;
        _lines = new TorrentAddLines(TorrentRules.DefaultRoute(settings.TorrentRoute, _providerName is not null), settings.TorrentChooseFiles, _providerName);
        _magnetNote.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetName(_selectAll, "Select all torrents");
    }

    /// <summary>Set when <see cref="ShowAsync"/> gave no choice because the dialog never showed, rather than because it was cancelled.</summary>
    public bool CouldNotOpen { get; private set; }

    /// <param name="focus"><c>magnet</c> (title "Add magnet", a paste box), <c>torrentFiles</c> ("Import torrent file", no
    /// paste box) or <c>mixed</c> ("Add torrents", a paste box).</param>
    /// <param name="inputs">The starting lines: magnets and full paths of .torrent files on this PC.</param>
    public async Task<TorrentAddChoice?> ShowAsync(XamlRoot root, ElementTheme theme, string focus, IReadOnlyList<string> inputs)
    {
        var withPaste = focus is "magnet" or "mixed";
        var panel = new StackPanel { Spacing = 10, MinWidth = 640, Padding = new Thickness(0, 0, 14, 0) };
        if (withPaste)
        {
            _paste = new TextBox
            {
                Header = focus == "magnet" ? "Paste magnet links" : "Paste magnet links or .torrent file paths",
                PlaceholderText = focus == "magnet" ? "magnet:?xt=urn:btih:…" : "Magnet links, and .torrent file paths one per line",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 96,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_paste, ScrollBarVisibility.Auto);
            AutomationProperties.SetName(_paste, focus == "magnet" ? "Magnet links" : "Magnet links or .torrent file paths");
            _pasteTimer = new DispatcherOneShot(DispatcherQueue.GetForCurrentThread(), PasteDelay, SyncPaste);
            _paste.TextChanged += (_, _) => _pasteTimer.Restart();
            panel.Children.Add(_paste);
        }

        _status.Text.Style = InboundUi.Style("InboundSecondaryTextStyle");
        _status.Text.VerticalAlignment = VerticalAlignment.Center;
        _selectAll.Click += (_, _) =>
        {
            _lines.ToggleAll();
            Changed();
        };
        var selectRow = new Grid { ColumnSpacing = 12 };
        selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectRow.Children.Add(_selectAll);
        Grid.SetColumn(_status.Text, 1);
        selectRow.Children.Add(_status.Text);
        panel.Children.Add(selectRow);

        // The lines scroll inside their card; an open file tree scrolls inside its line.
        _listCard = InboundUi.Card(new ScrollViewer { Content = _list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, new Thickness(2));
        _listCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_listCard);
        if (_providerName is not null)
        {
            // Review 5:T1b: one short line on every add; the longer words are in its tooltip.
            var routeInfo = InboundUi.Secondary(
                $"This PC: other peers see this PC's address. {_providerName}: {_providerName} downloads it, then the files come over HTTPS.");
            ToolTipService.SetToolTip(routeInfo,
                $"This PC downloads from other peers with this PC's own address. {_providerName} downloads it on {_providerName}'s side, then fetches the files over HTTPS like any {_providerName} download.");
            routeInfo.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(routeInfo);
        }

        _magnetNote.Visibility = Visibility.Collapsed;
        panel.Children.Add(_magnetNote);

        var (destinationRow, destination, destinationError) = AddLinksDialog.DestinationRow(string.Empty, _windowHandle);
        destination.Header = "Save to";
        // T14: what an empty Save to means follows the torrent folder settings.
        var emptySaveTo = TorrentFolderText.SaveToPlaceholder(_settings);
        destination.PlaceholderText = emptySaveTo;
        AutomationProperties.SetName(destination, $"Save to, empty for {char.ToLowerInvariant(emptySaveTo[0])}{emptySaveTo[1..]}");
        panel.Children.Add(destinationRow);
        panel.Children.Add(destinationError.Text);
        _destination = destination;
        if (_withControls)
        {
            // T3: each read line says the free space where it goes; a folder typed in is asked about once typing stops.
            _spaceTimer = new DispatcherOneShot(DispatcherQueue.GetForCurrentThread(), TimeSpan.FromMilliseconds(600), AskSpaceAgain);
            destination.TextChanged += (_, _) => _spaceTimer.Restart();
        }

        var startMode = new ComboBox { Header = "Start", HorizontalAlignment = HorizontalAlignment.Stretch };
        startMode.Items.Add("Start now");
        startMode.Items.Add("Download later");
        startMode.SelectedIndex = 0;
        AutomationProperties.SetName(startMode, "Start");
        var queues = _settings.Queues.OrderBy(queue => queue.Order).ToList();
        var queueBox = new ComboBox { Header = "Queue", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var queue in queues) queueBox.Items.Add(queue.Name);
        queueBox.SelectedIndex = queues.Count == 0 ? -1 : Math.Max(0, queues.FindIndex(queue => queue.Id == _settings.DefaultQueueId));
        AutomationProperties.SetName(queueBox, "Queue");
        var startRow = new Grid { ColumnSpacing = 8 };
        startRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        startRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(queueBox, 1);
        startRow.Children.Add(startMode);
        startRow.Children.Add(queueBox);
        panel.Children.Add(startRow);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = focus switch
            {
                "magnet" => "Add magnet",
                "torrentFiles" => "Import torrent file",
                _ => "Add torrents",
            },
            // T13: the content scrolls and follows the window's height, so Add and Cancel always show.
            Content = DialogLayout.ScrollingContent(panel, root),
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        if (InboundUi.Style("AccentButtonStyle") is { } accent) dialog.PrimaryButtonStyle = accent;
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        _dialog = dialog;

        foreach (var line in _lines.Add(inputs, fromPaste: false)) Ask(line);
        Changed();

        dialog.Opened += (_, _) =>
        {
            if (_paste is not null) _paste.Focus(FocusState.Programmatic);
            else if (_rows.Values.FirstOrDefault() is { } first) first.Box.Focus(FocusState.Programmatic);
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            // Text pasted a moment ago becomes lines first, so it is seen before anything is added.
            if (_paste is not null && _paste.Text != _pastedText)
            {
                SyncPaste();
                args.Cancel = true;
                return;
            }

            if (!_lines.CanAdd)
            {
                args.Cancel = true;
                return;
            }

            var folder = destination.Text.Trim();
            if (folder.Length > 0 && !AddLinksDialog.IsFullPath(folder))
            {
                destinationError.Show(AddLinksDialog.FullPathWords, isProblem: true);
                destination.Focus(FocusState.Programmatic);
                args.Cancel = true;
            }
        };

        ContentDialogResult? answer;
        try
        {
            answer = await DialogGuard.ShowAsync(dialog);
        }
        finally
        {
            Close();
        }

        if (answer is null)
        {
            // Refused or failed: another dialog is already open over this window (UI.B8).
            CouldNotOpen = true;
            return null;
        }

        // "Show in list" on a line already in the list: the main window shows that row; nothing is added.
        if (_showJobId is { } listed) return TorrentAddChoice.ShowJob(listed) with { ListedInputs = _lines.ListedInputs() };
        if (answer != ContentDialogResult.Primary) return null;
        var picks = _lines.Picks();
        if (picks.Count == 0) return null;
        var chosenFolder = destination.Text.Trim();
        int? queueId = queues.Count == 0 ? null : queues[Math.Clamp(queueBox.SelectedIndex, 0, queues.Count - 1)].Id;
        return new TorrentAddChoice(picks, chosenFolder.Length > 0 ? chosenFolder : null, startMode.SelectedIndex == 1 ? "later" : "startNow", queueId)
        {
            ListedInputs = _lines.ListedInputs(),
        };
    }

    /// <summary>Closes the dialog to show the job that already holds a line's torrent.</summary>
    private void ShowListed(Guid id)
    {
        if (_closed || _dialog is null) return;
        _showJobId = id;
        _dialog.Hide();
    }

    /// <summary>Stops every question still out; their answers are not wanted any more.</summary>
    private void Close()
    {
        _closed = true;
        _pasteTimer?.Stop();
        _spaceTimer?.Stop();
        try
        {
            _closing.Cancel();
        }
        catch (AggregateException)
        {
            // A question's own cancel step failed; it is not waited for either way.
        }

        _asking.Clear();
    }

    private void SyncPaste()
    {
        if (_closed || _paste is null) return;
        _pasteTimer?.Stop();
        _pastedText = _paste.Text;
        var (added, removed) = _lines.SyncPaste(_pastedText);
        foreach (var line in removed)
        {
            if (_asking.Remove(line, out var asking)) asking.Cancel();
        }

        foreach (var line in added) Ask(line);
        if (added.Count > 0 || removed.Count > 0) Changed();
        else Refresh();
    }

    /// <summary>Asks the Host about a line. The answer comes back on this (the UI) thread.</summary>
    private async void Ask(TorrentAddLine line)
    {
        CancellationTokenSource asking;
        try
        {
            asking = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _asking[line] = asking;
        var token = asking.Token;
        try
        {
            await _turns.WaitAsync(token);
            TorrentInspectResult result;
            var folder = _withControls ? _spaceFolder : null;
            try
            {
                result = await _remote.InspectTorrentAsync(line.Input, token, folder, _jobFor?.Invoke(line.Input));
            }
            finally
            {
                _turns.Release();
            }

            if (_closed || token.IsCancellationRequested) return;
            if (_lines.Inspected(line, result, askedWithoutFolder: folder is null)) Changed();
        }
        catch (OperationCanceledException)
        {
            // The dialog closed or the line went.
        }
        catch (Exception ex)
        {
            // Never the input in the trace: it may be a magnet with trackers.
            UiTrace.Write($"Torrent dialog: reading a line failed ({ex.GetType().Name}).");
            if (!_closed && _lines.Inspected(line, new TorrentInspectResult { Error = "This torrent couldn't be read." })) Changed();
        }
        finally
        {
            if (_asking.TryGetValue(line, out var current) && ReferenceEquals(current, asking)) _asking.Remove(line);
            asking.Dispose();
        }
    }

    /// <summary>
    /// T3: the destination box changed. Once it holds a full path (or is empty again, each category folder) the read
    /// lines are asked about that folder, and only their free space is taken from the answer.
    /// </summary>
    private void AskSpaceAgain()
    {
        if (_closed || _destination is null) return;
        var text = _destination.Text.Trim();
        string? folder = text.Length == 0 ? null : AddLinksDialog.IsFullPath(text) ? text : _spaceFolder;
        if (folder == _spaceFolder) return;
        _spaceFolder = folder;
        foreach (var line in _lines.Lines.Where(line => line.State == TorrentLineState.Ready).ToList())
            _ = AskSpaceAsync(line, folder);
    }

    private async Task AskSpaceAsync(TorrentAddLine line, string? folder)
    {
        try
        {
            await _turns.WaitAsync(_closing.Token);
            TorrentInspectResult answer;
            try
            {
                answer = await _remote.InspectTorrentAsync(line.Input, _closing.Token, folder, _jobFor?.Invoke(line.Input));
            }
            finally
            {
                _turns.Release();
            }

            if (_closed || folder != _spaceFolder || !_lines.IsListed(line)) return;
            // The files and the choice stay as read; only where it goes and what is free there change.
            if (_lines.SpaceAnswered(line, folder, answer) && _rows.TryGetValue(line, out var row)) row.Update();
        }
        catch (OperationCanceledException)
        {
            // The dialog closed.
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Torrent dialog: the free space wasn't read ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// With an account that takes torrents, each read line is asked about once (T1 step 6): one connection, one line at
    /// a time, closed as soon as nothing is left to ask. The answer only sets the line's cache words.
    /// </summary>
    private void StartCacheChecks()
    {
        foreach (var check in _lines.StartCacheChecks()) _cacheChecks.Enqueue(check);
        if (_cacheChecks.Count > 0 && !_checkingCache) _ = CheckCacheAsync();
    }

    private async Task CheckCacheAsync()
    {
        _checkingCache = true;
        IRemoteClassifier? classifier = null;
        TorrentAddLine? asking = null;
        try
        {
            while (!_closed && _cacheChecks.TryDequeue(out var check))
            {
                if (!_lines.IsListed(check.Line)) continue;
                asking = check.Line;
                classifier ??= _remote.OpenClassifier(withFiles: true);
                var result = classifier.Broken
                    ? new RemoteClassifyResult { Error = RemoteText.ClassifyLost }
                    : await classifier.ClassifyAsync(check.Query, _closing.Token);
                if (_closed) return;
                asking = null;
                if (_lines.CacheChecked(check.Line, result)) Changed();
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog closed.
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Torrent dialog: a cache check failed ({ex.GetType().Name}).");
            if (!_closed)
            {
                if (asking is not null) _lines.CacheChecked(asking, null);
                while (_cacheChecks.TryDequeue(out var check)) _lines.CacheChecked(check.Line, null);
                Changed();
            }
        }
        finally
        {
            if (classifier is not null)
            {
                try
                {
                    await classifier.DisposeAsync();
                }
                catch (Exception ex)
                {
                    UiTrace.Write($"Torrent dialog: closing the cache check failed ({ex.GetType().Name}).");
                }
            }

            // Cleared only once the connection is closed, so a line read meanwhile never starts a second loop.
            _checkingCache = false;
        }

        // A line read while the connection was closing.
        if (!_closed && _cacheChecks.Count > 0) _ = CheckCacheAsync();
    }

    /// <summary>The lines changed: rows for new lines, none for gone ones, and every row brought up to date.</summary>
    private void Changed()
    {
        if (_closed) return;
        StartCacheChecks();
        var lines = _lines.Lines;
        foreach (var gone in _rows.Keys.Except(lines).ToList()) _rows.Remove(gone);
        var shown = new List<UIElement>(lines.Count);
        foreach (var line in lines)
        {
            if (!_rows.TryGetValue(line, out var row))
            {
                row = new LineRow(line, this);
                _rows[line] = row;
            }

            row.Update();
            shown.Add(row.Root);
        }

        // Rows are put back only when the lines themselves changed, so a ticked box keeps its focus.
        if (!_list.Children.SequenceEqual(shown))
        {
            _list.Children.Clear();
            foreach (var root in shown) _list.Children.Add(root);
        }

        Refresh();
    }

    /// <summary>The words and buttons under the lines.</summary>
    private void Refresh()
    {
        if (_dialog is null || _closed) return;
        var lines = _lines.Lines;
        _status.Show(_lines.Status());
        _dialog.IsPrimaryButtonEnabled = _lines.CanAdd;
        _dialog.PrimaryButtonText = _lines.PrimaryText;
        if (_listCard is not null) _listCard.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _selectAll.Visibility = lines.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _selectAll.IsEnabled = lines.Any(line => line.CanSelect);
        _selectAll.IsChecked = _lines.SelectAllState();
        _magnetNote.Visibility = _lines.MagnetNoteApplies ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One line: the box with the name and the words under it, the This PC / provider choice on the right, and for a
    /// .torrent with several files going to This PC, "Choose files…" with the file tree under it.
    /// </summary>
    private sealed class LineRow
    {
        private readonly TorrentAddLine _line;
        private readonly TorrentAddDialog _owner;
        private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = RowTextWidth };
        private readonly ProgressRing _reading = new() { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed };
        private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = RowTextWidth };

        // T9 W3: the provider's cache answer as a badge under the route choice; collapsed without an account.
        private readonly CacheBadgeView _cache = new();

        // T9: a .torrent going to the provider takes the ticked files; its priorities are This PC's only.
        private readonly TextBlock _priorityNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(28, 0, 0, 0) };

        // T3: the free space where it goes, and a problem when its files don't fit.
        private readonly TextBlock _space = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = RowTextWidth, Visibility = Visibility.Collapsed };
        private readonly RadioButtons? _route;
        private readonly HyperlinkButton _chooseFiles = new() { Content = "Choose files…", Visibility = Visibility.Collapsed, Padding = new Thickness(4, 2, 4, 2) };
        private readonly HyperlinkButton _showInList = new() { Content = "Show in list", Visibility = Visibility.Collapsed, Padding = new Thickness(4, 2, 4, 2) };
        private readonly Border _treeHost = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(28, 4, 0, 4) };
        private RemoteFileTreeView? _tree;
        private bool _treeOpen;
        private bool _updating;

        public LineRow(TorrentAddLine line, TorrentAddDialog owner)
        {
            _line = line;
            _owner = owner;
            var under = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _reading, _detail } };
            Box = new CheckBox { Content = new StackPanel { Children = { _name, under, _space } }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Box.Click += (_, _) =>
            {
                owner._lines.Select(line, Box.IsChecked == true);
                owner.Changed();
            };
            ToolTipService.SetToolTip(_name, line.ToolTip);

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Box);
            if (owner._providerName is { } provider)
            {
                _route = new RadioButtons { MaxColumns = 2, VerticalAlignment = VerticalAlignment.Center };
                _route.Items.Add("This PC");
                _route.Items.Add(provider);
                _route.SelectionChanged += (_, _) =>
                {
                    if (_updating || _route.SelectedIndex < 0) return;
                    owner._lines.SetRoute(line, _route.SelectedIndex == 0 ? TorrentLineRoute.ThisPc : TorrentLineRoute.Provider);
                    owner.Changed();
                };
                // The badge sits under the choice it is about, the provider's.
                _cache.Element.HorizontalAlignment = HorizontalAlignment.Right;
                var side = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _route, _cache.Element } };
                Grid.SetColumn(side, 1);
                grid.Children.Add(side);
            }

            _chooseFiles.Margin = new Thickness(24, 0, 0, 0);
            _chooseFiles.Click += (_, _) =>
            {
                _treeOpen = !_treeOpen;
                Update();
            };
            _showInList.Margin = new Thickness(24, 0, 0, 0);
            _showInList.Click += (_, _) =>
            {
                if (line.ListedJobId is { } id) owner.ShowListed(id);
            };
            _priorityNote.Text = $"{owner._providerName} takes the ticked files. Priorities apply on This PC only.";
            Root = new StackPanel { Spacing = 2, Children = { grid, _showInList, _chooseFiles, _treeHost, _priorityNote } };
        }

        public StackPanel Root { get; }

        public CheckBox Box { get; }

        public void Update()
        {
            _updating = true;
            try
            {
                var name = _line.Name;
                _name.Text = name;
                ToolTipService.SetToolTip(_name, _line.ToolTip);
                _detail.Text = _line.Detail;
                _detail.Foreground = InboundTheme.Brush(_line.IsProblem ? "InboundErrorTextBrush" : "InboundSecondaryTextBrush", _owner._themeRoot);
                var reading = _line.State == TorrentLineState.Reading;
                var cache = _line.CacheText(_owner._providerName);
                _cache.Show(_line.Badge(_owner._providerName));
                _reading.IsActive = reading;
                _reading.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
                AutomationProperties.SetName(_reading, _line.Detail);
                var space = _owner._withControls ? TorrentAddLines.SpaceOf(_line) : null;
                _space.Text = space?.Words ?? string.Empty;
                _space.Foreground = InboundTheme.Brush(space?.IsProblem == true ? "InboundErrorTextBrush" : "InboundSecondaryTextBrush", _owner._themeRoot);
                _space.Visibility = space is null ? Visibility.Collapsed : Visibility.Visible;

                Box.IsChecked = _line.IsSelected;
                Box.IsEnabled = _line.CanSelect;
                AutomationProperties.SetName(Box, cache.Length > 0 ? $"{name}, {_line.Detail}, {cache}" : $"{name}, {_line.Detail}");
                _showInList.Visibility = _line.ListedJobId is not null ? Visibility.Visible : Visibility.Collapsed;
                AutomationProperties.SetName(_showInList, $"Show {name} in the list");

                if (_route is not null)
                {
                    _route.SelectedIndex = _line.Route switch
                    {
                        TorrentLineRoute.ThisPc => 0,
                        TorrentLineRoute.Provider => 1,
                        _ => -1,
                    };
                    _route.IsEnabled = _line.State is TorrentLineState.Ready or TorrentLineState.Reading;
                    AutomationProperties.SetName(_route, $"Download {name} with");
                }

                UpdateFiles(name);
            }
            finally
            {
                _updating = false;
            }
        }

        private void UpdateFiles(string name)
        {
            var applies = _owner._lines.CanChooseFilesFor(_line);
            // Review 5:T1a: the files stay closed until Choose files… is clicked, a lone torrent's too (all its files are
            // ticked), so Save to, Start and Queue stay in view, as in the web UI's add dialog.

            _chooseFiles.Visibility = applies ? Visibility.Visible : Visibility.Collapsed;
            _chooseFiles.Content = _treeOpen ? "Hide files" : "Choose files…";
            AutomationProperties.SetName(_chooseFiles, _treeOpen ? $"Hide the files of {name}" : $"Choose the files of {name}");

            var showTree = applies && _treeOpen;
            if (showTree && _tree is null && _line.Result?.Files is { } files)
            {
                // Virtualized rows; the height cap is what lets the list virtualize inside the dialog's scrolling.
                _tree = new RemoteFileTreeView { MaxHeight = 260, ShowPriorities = _owner._withControls };
                _tree.SetFiles(TorrentRules.TreeFiles(files), chooseAll: true);
                _tree.SelectionChanged += (_, _) =>
                {
                    if (_tree is null) return;
                    _owner._lines.SetFileChoice(_line, _tree.Tree.Choice, _owner._withControls ? _tree.Tree.Priorities : null);
                    Update();
                    _owner.Refresh();
                };
                _treeHost.Child = _tree;
            }

            if (_tree is not null) AutomationProperties.SetName(_tree, $"Files of {name}");
            _treeHost.Visibility = showTree && _tree is not null ? Visibility.Visible : Visibility.Collapsed;
            _priorityNote.Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _owner._themeRoot);
            _priorityNote.Visibility = showTree && _owner._withControls && _line.Route == TorrentLineRoute.Provider ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
