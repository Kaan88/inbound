using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One chosen line. The password leaves its box only for the one message that carries it.</summary>
internal sealed class AddLinksPick(AddLine line, string? linkPassword, bool linkPasswordForArchives = false)
{
    public AddLine Line { get; } = line;

    public string? LinkPassword { get; set; } = linkPassword;

    /// <summary>TB.S2: the user ticked "also try it on this link's own archives".</summary>
    public bool LinkPasswordForArchives { get; } = linkPassword is not null && linkPasswordForArchives;

    public override string ToString() => "[add pick]";
}

/// <summary>What the add dialog was told to do.</summary>
internal sealed class AddLinksChoice
{
    public required IReadOnlyList<AddLinksPick> Picks { get; init; }
    public string? Destination { get; init; }
    public bool StartLater { get; init; }
    public int QueueId { get; init; }
    public int ConflictIndex { get; init; }
    public bool KeepPackage { get; init; }

    /// <summary>"Let me choose files when ... is done" was on show and ticked.</summary>
    public bool ChooseLater { get; init; }

    /// <summary>For the package ordinary links go into, for the one message that sets it.</summary>
    public string? ArchivePassword { get; set; }

    public override string ToString() => "[add choice]";
}

/// <summary>
/// Add Links, Add Magnet and Import Torrent File once a provider account can take them. Every line says
/// what it is as soon as the Host does, and only then can it be added. A hoster link only ever goes to
/// the provider: no control here can send one anywhere else. Destination, queue and start are the same
/// controls as in the ordinary dialog, and with no usable account that dialog is what opens instead.
/// </summary>
internal sealed class AddLinksDialog
{
    private const double RowTextWidth = 590;

    private readonly AddLineSession _session;
    private readonly AddFocus _focus;
    private readonly SettingsDto _settings;
    private readonly string _provider;
    private readonly bool _archivesSupported;
    private readonly FrameworkElement _themeRoot;
    private readonly IntPtr _windowHandle;
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly Dictionary<AddLine, LineRow> _rows = [];
    private readonly StatusLine _status = new("Links status");
    private readonly TextBox _paste;
    private readonly CheckBox _selectAll = new() { Content = "Select all", Visibility = Visibility.Collapsed };
    private readonly CheckBox _chooseLater = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _directOptions = new() { Spacing = 10 };
    private readonly CheckBox _keepPackage = new() { Content = "Keep as one package", IsChecked = true };
    private readonly StackPanel _archivePanel = new() { Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly PasswordBox _archivePassword = new() { Header = "Archive password for this package (optional)", PasswordRevealMode = PasswordRevealMode.Hidden };
    private ContentDialog? _dialog;

    public AddLinksDialog(AddLineSession session, AddFocus focus, SettingsDto settings, string provider, bool archivesSupported, FrameworkElement themeRoot, IntPtr windowHandle)
    {
        _session = session;
        _focus = focus;
        _settings = settings;
        _provider = provider;
        _archivesSupported = archivesSupported;
        _themeRoot = themeRoot;
        _windowHandle = windowHandle;
        _paste = new TextBox
        {
            Header = focus == AddFocus.Magnet ? "Paste magnet links" : "Paste links or any text containing links",
            PlaceholderText = focus == AddFocus.Magnet ? "magnet:?xt=urn:btih:…" : "Links, folder links and magnets, one per line",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_paste, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_paste, focus == AddFocus.Magnet ? "Magnet links" : "Links");

        // Off by default: left alone, what isn't cached takes all its files.
        _chooseLater.Content = $"Let me choose files when {provider} is done";
        AutomationProperties.SetName(_chooseLater, $"Let me choose files when {provider} is done");
        AutomationProperties.SetName(_archivePassword, "Archive password for this package");
    }

    /// <summary>Set when <see cref="ShowAsync"/> gave no choice because the dialog never showed, rather than because it was cancelled.</summary>
    public bool CouldNotOpen { get; private set; }

    public async Task<AddLinksChoice?> ShowAsync(XamlRoot root, ElementTheme theme, string? initialText)
    {
        var (destinationRow, destination, destinationError) = DestinationRow(_settings.DefaultDownloadDirectory, _windowHandle);
        var startMode = new ComboBox { Header = "Start", HorizontalAlignment = HorizontalAlignment.Stretch };
        startMode.Items.Add("Start now");
        startMode.Items.Add("Download later");
        startMode.SelectedIndex = 0;
        var queues = _settings.Queues.OrderBy(queue => queue.Order).ToList();
        var queueBox = new ComboBox { Header = "Queue", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var queue in queues) queueBox.Items.Add(queue.Name);
        queueBox.SelectedIndex = Math.Max(0, queues.FindIndex(queue => queue.Id == _settings.DefaultQueueId));
        var startRow = new Grid { ColumnSpacing = 8 };
        startRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        startRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(queueBox, 1);
        startRow.Children.Add(startMode);
        startRow.Children.Add(queueBox);

        var conflictBox = new ComboBox { Header = "If a file exists", HorizontalAlignment = HorizontalAlignment.Stretch };
        conflictBox.Items.Add("Rename the new file");
        conflictBox.Items.Add("Overwrite the existing file");
        conflictBox.Items.Add("Fail the new download");
        conflictBox.SelectedIndex = 0;
        var skipListed = new CheckBox { Content = "Skip URLs that are already in the list", IsChecked = true };
        skipListed.Click += (_, _) => _session.SkipListed = skipListed.IsChecked == true;
        _keepPackage.Click += (_, _) => Refresh();
        BuildArchivePanel();
        _directOptions.Children.Add(new TextBlock { Text = "For ordinary links", FontSize = 12, Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _themeRoot) });
        _directOptions.Children.Add(conflictBox);
        _directOptions.Children.Add(skipListed);
        _directOptions.Children.Add(_keepPackage);
        _directOptions.Children.Add(_archivePanel);

        // By the time this runs the box has moved on by itself, and from mixed it goes to off. The lines decide instead.
        _selectAll.Click += (_, _) =>
        {
            _session.ToggleAll();
            Refresh();
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 640, Padding = new Thickness(0, 0, 14, 0) };
        if (_focus is AddFocus.Links or AddFocus.Magnet) panel.Children.Add(_paste);
        panel.Children.Add(_status.Text);
        panel.Children.Add(_selectAll);
        panel.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(_chooseLater);
        panel.Children.Add(destinationRow);
        panel.Children.Add(destinationError.Text);
        panel.Children.Add(startRow);
        if (_focus == AddFocus.Links) panel.Children.Add(_directOptions);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = _focus switch
            {
                AddFocus.Magnet => "Add magnet",
                AddFocus.TorrentFiles => "Import torrent file",
                AddFocus.OneAddress => "Add link",
                AddFocus.ImportedLinks => "Import download list",
                _ => "Add links",
            },
            Content = new ScrollViewer
            {
                Padding = InboundTheme.DialogScrollPadding,
                Content = panel,
                MaxHeight = Math.Max(240, Math.Min(620, _themeRoot.ActualHeight - 200)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        _dialog = dialog;

        _session.LinesChanged += SyncRows;
        _session.LineChanged += OnLineChanged;
        var opening = true;
        _paste.TextChanged += (_, _) =>
        {
            if (opening) return;
            _session.SetText(_paste.Text);
            Refresh();
        };
        if (!string.IsNullOrEmpty(initialText))
        {
            _paste.Text = initialText;
            _session.SetTextNow(initialText);
        }

        opening = false;
        SyncRows();
        dialog.Opened += (_, _) =>
        {
            if (_focus is AddFocus.Links or AddFocus.Magnet) _paste.Focus(FocusState.Programmatic);
        };

        // Checked while it can still be answered in place. Only the Provider routes need a full path.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var folder = destination.Text.Trim();
            var needsFullPath = _session.Chosen.Any(line => line.Route is AddRoute.Provider or AddRoute.FolderLink);
            if (needsFullPath && folder.Length > 0 && !IsFullPath(folder))
            {
                destinationError.Show("The destination has to be a full path, such as D:\\Downloads.", isProblem: true);
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
            _session.LinesChanged -= SyncRows;
            _session.LineChanged -= OnLineChanged;
        }

        if (answer is null)
        {
            // Refused or failed: another dialog is already open over this window (UI.B8).
            CouldNotOpen = true;
            ClearPasswords();
            return null;
        }

        if (answer != ContentDialogResult.Primary)
        {
            ClearPasswords();
            return null;
        }

        var picks = _session.Chosen.Select(line => new AddLinksPick(line, TakeLinkPassword(line), LinkPasswordForArchives(line))).ToList();
        var archivePassword = _archivePanel.Visibility == Visibility.Visible ? _archivePassword.Password : string.Empty;
        ClearPasswords();
        return new AddLinksChoice
        {
            Picks = picks,
            Destination = destination.Text.Trim() is { Length: > 0 } chosenFolder ? chosenFolder : null,
            StartLater = startMode.SelectedIndex == 1,
            QueueId = queues.ElementAtOrDefault(Math.Max(0, queueBox.SelectedIndex))?.Id ?? _settings.DefaultQueueId,
            ConflictIndex = conflictBox.SelectedIndex,
            KeepPackage = _keepPackage.IsChecked == true,
            ChooseLater = _chooseLater.Visibility == Visibility.Visible && _chooseLater.IsChecked == true,
            ArchivePassword = archivePassword.Length > 0 ? archivePassword : null,
        };
    }

    private void BuildArchivePanel()
    {
        _archivePanel.Children.Add(_archivePassword);
        _archivePanel.Children.Add(new TextBlock
        {
            Text = "For RAR archives in this package, used when they are extracted. It is not a link password, and it never leaves this computer.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _themeRoot),
        });
    }

    internal static bool IsFullPath(string folder)
    {
        try
        {
            return Path.IsPathFullyQualified(folder);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A folder box with Browse and the line that says what is wrong with it. Used wherever a provider's work gets a destination.</summary>
    internal static (UIElement Row, TextBox Box, StatusLine Error) DestinationRow(string initial, IntPtr windowHandle)
    {
        var box = new TextBox { Header = "Destination folder", Text = initial };
        AutomationProperties.SetName(box, "Destination folder");
        var error = new StatusLine("Destination status");
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetName(browse, "Browse for the destination folder");

        // Browse follows the box, so a caller that has settled on a folder only has one thing to turn off.
        var picking = false;
        box.IsEnabledChanged += (_, _) => browse.IsEnabled = box.IsEnabled && !picking;
        browse.Click += async (_, _) =>
        {
            picking = true;
            browse.IsEnabled = false;
            error.Show(string.Empty);
            try
            {
                if (await Pickers.PickFolderAsync(windowHandle) is { } folder && box.IsEnabled) box.Text = folder;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                error.Show("The folder picker couldn't open. Type the folder instead.", isProblem: true);
            }
            finally
            {
                picking = false;
                browse.IsEnabled = box.IsEnabled;
            }
        };
        box.TextChanged += (_, _) => error.Show(string.Empty);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(browse, 1);
        row.Children.Add(box);
        row.Children.Add(browse);
        return (FolderChooser.Attach(row, box), box, error);
    }

    private string? TakeLinkPassword(AddLine line)
    {
        if (!_rows.TryGetValue(line, out var row) || row.HasPassword.IsChecked != true || line.Result?.Kind != "hosterLink") return null;
        var password = row.Password.Password;
        return password.Length > 0 ? password : null;
    }

    private bool LinkPasswordForArchives(AddLine line)
        => _rows.TryGetValue(line, out var row) && row.HasPassword.IsChecked == true && row.ForArchives.IsChecked == true;

    private void ClearPasswords()
    {
        _archivePassword.Password = string.Empty;
        foreach (var row in _rows.Values) row.Password.Password = string.Empty;
    }

    private void SyncRows()
    {
        var lines = _session.Lines;
        foreach (var gone in _rows.Keys.Except(lines).ToList())
        {
            _rows[gone].Password.Password = string.Empty;
            _rows.Remove(gone);
        }

        _list.Children.Clear();
        foreach (var line in lines)
        {
            if (!_rows.TryGetValue(line, out var row))
            {
                row = new LineRow(line, _session, _provider, _themeRoot, Refresh);
                _rows[line] = row;
            }

            row.Update();
            _list.Children.Add(row.Root);
        }

        Refresh();
    }

    private void OnLineChanged(AddLine line)
    {
        if (_rows.TryGetValue(line, out var row)) row.Update();
        Refresh();
    }

    private void Refresh()
    {
        if (_dialog is null) return;
        var lines = _session.Lines;
        var (text, isProblem) = RemoteText.AddSummary(lines, _focus, !string.IsNullOrWhiteSpace(_paste.Text));
        _status.Show(text, isProblem);
        _dialog.IsPrimaryButtonEnabled = _session.CanAdd;

        _selectAll.Visibility = lines.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _selectAll.IsEnabled = lines.Any(AddLineSession.CanSelect);
        _selectAll.IsChecked = AddLineSession.SelectAllState(lines);
        _chooseLater.Visibility = AddChoices.ChooseLaterApplies(lines) ? Visibility.Visible : Visibility.Collapsed;

        var direct = lines.Count(line => line.IsSelected && line.Route == AddRoute.Direct);
        _directOptions.Opacity = direct > 0 ? 1 : 0.6;
        foreach (var control in _directOptions.Children.OfType<Control>()) control.IsEnabled = direct > 0;
        _archivePanel.Visibility = _archivesSupported && direct >= 2 && _keepPackage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One line's controls. Dependent controls sit 28px in, under the box they belong to.</summary>
    private sealed class LineRow
    {
        private readonly AddLine _line;
        private readonly string _provider;
        private readonly FrameworkElement _themeRoot;
        private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = RowTextWidth };
        private readonly TextBlock _word = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = RowTextWidth };
        private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = RowTextWidth };
        private readonly Button _retry = new() { Content = "Try again", Visibility = Visibility.Collapsed };
        private readonly CheckBox _direct = new() { Visibility = Visibility.Collapsed };
        private readonly StackPanel _extras = new() { Spacing = 6, Margin = new Thickness(28, 0, 0, 0) };

        public LineRow(AddLine line, AddLineSession session, string provider, FrameworkElement themeRoot, Action changed)
        {
            _line = line;
            _provider = provider;
            _themeRoot = themeRoot;
            Box = new CheckBox { Content = new StackPanel { Children = { _name, _word, _detail } }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Box.Click += (_, _) => session.Select(line, Box.IsChecked == true);
            HasPassword = new CheckBox { Content = "This link has a password", Visibility = Visibility.Collapsed };
            Password = new PasswordBox
            {
                Header = "The password the site set for this link. It isn't an archive password.",
                PasswordRevealMode = PasswordRevealMode.Hidden,
                Visibility = Visibility.Collapsed,
                MaxWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            // TB.S2: never ticked for the user. Only this link's own archives, and an archive password given wins.
            ForArchives = new CheckBox { Content = "Also try it on this link's own archives", Visibility = Visibility.Collapsed };
            HasPassword.Click += (_, _) =>
            {
                Password.Visibility = HasPassword.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                ForArchives.Visibility = Password.Visibility;
                if (HasPassword.IsChecked != true)
                {
                    Password.Password = string.Empty;
                    ForArchives.IsChecked = false;
                }

                changed();
            };
            _retry.Click += (_, _) => session.Retry(line);
            _direct.Click += (_, _) => session.ChooseDirect(line, _direct.IsChecked == true);
            _extras.Children.Add(_retry);
            _extras.Children.Add(_direct);
            _extras.Children.Add(HasPassword);
            _extras.Children.Add(Password);
            _extras.Children.Add(ForArchives);
            Root = new StackPanel { Spacing = 2, Children = { Box, _extras } };
        }

        public StackPanel Root { get; }
        public CheckBox Box { get; }
        public CheckBox HasPassword { get; }
        public PasswordBox Password { get; }
        public CheckBox ForArchives { get; }

        public void Update()
        {
            var view = RemoteText.AddLineWords(_line, _provider);
            _name.Text = view.Name;
            _word.Text = view.Word;
            _detail.Text = view.Detail;
            _detail.Visibility = view.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _detail.Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _themeRoot);
            if (view.IsProblem) _word.Foreground = InboundTheme.Brush("InboundErrorTextBrush", _themeRoot);
            else _word.ClearValue(TextBlock.ForegroundProperty);

            Box.IsChecked = _line.IsSelected;
            Box.IsEnabled = view.CanSelect;
            AutomationProperties.SetName(Box, view.AccessibleName);
            ToolTipService.SetToolTip(Box, view.ToolTip);

            _retry.Visibility = view.CanRetry ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(_retry, $"Try again: {view.Name}");
            var directWords = $"Download directly, not through {ProviderOf(view)}";
            _direct.Content = directWords;
            AutomationProperties.SetName(_direct, $"{directWords}: {view.Name}");
            _direct.Visibility = view.CanChooseDirect ? Visibility.Visible : Visibility.Collapsed;
            _direct.IsChecked = _line.DirectChosen;

            HasPassword.Visibility = view.CanTakeLinkPassword ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(HasPassword, $"{view.Name} has a password");
            AutomationProperties.SetName(Password, $"Link password for {view.Name}");
            AutomationProperties.SetName(ForArchives, $"Also try the link password on the archives of {view.Name}");
            if (!view.CanTakeLinkPassword)
            {
                HasPassword.IsChecked = false;
                Password.Password = string.Empty;
                Password.Visibility = Visibility.Collapsed;
                ForArchives.IsChecked = false;
                ForArchives.Visibility = Visibility.Collapsed;
            }

            _extras.Visibility = view.CanRetry || view.CanChooseDirect || view.CanTakeLinkPassword ? Visibility.Visible : Visibility.Collapsed;
        }

        private string ProviderOf(AddLineView view) => string.IsNullOrWhiteSpace(_line.Result?.ProviderName) ? _provider : _line.Result.ProviderName;
    }
}
