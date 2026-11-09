using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// One chosen line. The passwords leave their boxes only for the one message that carries them. The site's password and
/// the archive password are typed separately and never stand in for each other (owner, 2026-11-08).
/// </summary>
internal sealed class AddLinksPick(AddLine line, string? linkPassword = null, string? archivePassword = null)
{
    public AddLine Line { get; } = line;

    /// <summary>The site's password for this link, sent to the provider on create.</summary>
    public string? LinkPassword { get; set; } = linkPassword;

    /// <summary>For this link's own archives, kept encrypted by the Host.</summary>
    public string? ArchivePassword { get; set; } = archivePassword;

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
    private Border? _listCard;
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
        // UI.R31: Select all with the count beside it in secondary text, then the lines in a framed card.
        _status.Text.Style = InboundUi.Style("InboundSecondaryTextStyle");
        _status.Text.VerticalAlignment = VerticalAlignment.Center;
        var selectRow = new Grid { ColumnSpacing = 12 };
        selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectRow.Children.Add(_selectAll);
        Grid.SetColumn(_status.Text, 1);
        selectRow.Children.Add(_status.Text);
        panel.Children.Add(selectRow);
        _list.Padding = new Thickness(8, 6, 8, 6);
        _listCard = InboundUi.Card(new ScrollViewer { Content = _list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, new Thickness(2));
        _listCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_listCard);
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
                destinationError.Show(FullPathWords, isProblem: true);
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

        var picks = _session.Chosen.Select(PickOf).ToList();
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

    /// <summary>What a destination that isn't a full path is told, in the shown Host's own path style.</summary>
    internal static string FullPathWords => $"The destination has to be {ServerPaths.FullFolderWords(ShownServer.PathStyle)}.";

    /// <summary>A full path on this PC, or on the server the window shows, by its rules (<see cref="ShownServer.PathStyle"/>).</summary>
    internal static bool IsFullPath(string folder) => ServerPaths.IsFull(folder, ShownServer.PathStyle);

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

    private AddLinksPick PickOf(AddLine line)
        => _rows.TryGetValue(line, out var row) && line.Result?.Kind == "hosterLink"
            ? new AddLinksPick(line, row.TakeSitePassword(), row.TakeArchivePassword())
            : new AddLinksPick(line);

    private void ClearPasswords()
    {
        _archivePassword.Password = string.Empty;
        foreach (var row in _rows.Values) row.ClearPasswords();
    }

    private void SyncRows()
    {
        var lines = _session.Lines;
        foreach (var gone in _rows.Keys.Except(lines).ToList())
        {
            _rows[gone].ClearPasswords();
            _rows.Remove(gone);
        }

        _list.Children.Clear();
        foreach (var line in lines)
        {
            if (!_rows.TryGetValue(line, out var row))
            {
                row = new LineRow(line, _session, _provider, _archivesSupported, _themeRoot, Refresh);
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
        if (_listCard is not null) _listCard.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        private readonly bool _archivesSupported;
        private readonly FrameworkElement _themeRoot;
        private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = RowTextWidth };
        // UI.R31: what the line is, as a small chip.
        private readonly TextBlock _word = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = RowTextWidth };
        private readonly Border _wordChip = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 8, 1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
        private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = RowTextWidth };
        private readonly Button _retry = new() { Content = "Try again", Visibility = Visibility.Collapsed };
        private readonly CheckBox _direct = new() { Visibility = Visibility.Collapsed };
        private readonly StackPanel _extras = new() { Spacing = 6, Margin = new Thickness(28, 0, 0, 0) };

        public LineRow(AddLine line, AddLineSession session, string provider, bool archivesSupported, FrameworkElement themeRoot, Action changed)
        {
            _line = line;
            _provider = provider;
            _archivesSupported = archivesSupported;
            _themeRoot = themeRoot;
            _wordChip.Child = _word;
            Box = new CheckBox { Content = new StackPanel { Children = { _name, _wordChip, _detail } }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Box.Click += (_, _) => session.Select(line, Box.IsChecked == true);
            HasPassword = new CheckBox { Content = "It needs a password", Visibility = Visibility.Collapsed };
            // The owner, 2026-11-08: the archive password comes first, and the site's password is a box of its own,
            // because the two are often different. Nothing typed in one is ever used for the other. Without extraction
            // on this Host only the site's box is shown.
            ArchivePassword = PasswordField("Archive password: used when the downloaded archives are extracted");
            // Off until ticked: then the archive password is also sent to the site and the site's box goes away.
            SameForBoth = new CheckBox { Content = "Use it as the site password too", Visibility = Visibility.Collapsed };
            SitePassword = PasswordField("Site password: only if the link's page asks for one before it shows the file");
            HasPassword.Click += (_, _) =>
            {
                if (HasPassword.IsChecked != true) ClearPasswords();
                ShowPasswordBoxes();
                changed();
            };
            SameForBoth.Click += (_, _) =>
            {
                if (SameForBoth.IsChecked == true) SitePassword.Password = string.Empty;
                ShowPasswordBoxes();
                changed();
            };
            _retry.Click += (_, _) => session.Retry(line);
            _direct.Click += (_, _) => session.ChooseDirect(line, _direct.IsChecked == true);
            _extras.Children.Add(_retry);
            _extras.Children.Add(_direct);
            _extras.Children.Add(HasPassword);
            _extras.Children.Add(ArchivePassword);
            _extras.Children.Add(SameForBoth);
            _extras.Children.Add(SitePassword);
            Root = new StackPanel { Spacing = 2, Children = { Box, _extras } };
        }

        public StackPanel Root { get; }
        public CheckBox Box { get; }
        public CheckBox HasPassword { get; }

        /// <summary>For this link's own archives only. Shown only when this Host can extract.</summary>
        public PasswordBox ArchivePassword { get; }

        /// <summary>Sent to the provider as the link's own password, so it can open a password-protected hoster page.</summary>
        public PasswordBox SitePassword { get; }

        /// <summary>"Use it as the site password too": the archive password goes to the site as well.</summary>
        public CheckBox SameForBoth { get; }

        /// <summary>The site's password for this link, or null. With the tick, it is the archive password.</summary>
        public string? TakeSitePassword()
        {
            if (HasPassword.IsChecked != true) return null;
            var password = _archivesSupported && SameForBoth.IsChecked == true ? ArchivePassword.Password : SitePassword.Password;
            return password.Length > 0 ? password : null;
        }

        /// <summary>The archive password for this link, or null.</summary>
        public string? TakeArchivePassword()
            => HasPassword.IsChecked == true && _archivesSupported && ArchivePassword.Password.Length > 0 ? ArchivePassword.Password : null;

        public void ClearPasswords()
        {
            ArchivePassword.Password = string.Empty;
            SitePassword.Password = string.Empty;
            SameForBoth.IsChecked = false;
        }

        private void ShowPasswordBoxes()
        {
            var shown = HasPassword.IsChecked == true;
            ArchivePassword.Visibility = shown && _archivesSupported ? Visibility.Visible : Visibility.Collapsed;
            SameForBoth.Visibility = ArchivePassword.Visibility;
            var same = _archivesSupported && SameForBoth.IsChecked == true;
            SitePassword.Visibility = shown && !same ? Visibility.Visible : Visibility.Collapsed;
        }

        private static PasswordBox PasswordField(string header) => new()
        {
            Header = header,
            PlaceholderText = "Optional",
            PasswordRevealMode = PasswordRevealMode.Hidden,
            Visibility = Visibility.Collapsed,
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        public void Update()
        {
            var view = RemoteText.AddLineWords(_line, _provider);
            _name.Text = view.Name;
            _word.Text = view.Word;
            _detail.Text = view.Detail;
            _detail.Visibility = view.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _detail.Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _themeRoot);
            var chipBrush = InboundTheme.Brush(view.IsProblem ? "InboundErrorTextBrush" : "InboundSecondaryTextBrush", _themeRoot);
            _word.Foreground = chipBrush;
            _wordChip.BorderBrush = chipBrush;
            _wordChip.Visibility = view.Word.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

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
            AutomationProperties.SetName(HasPassword, $"{view.Name} needs a password");
            AutomationProperties.SetName(ArchivePassword, $"Archive password for {view.Name}");
            AutomationProperties.SetName(SitePassword, $"Site password for {view.Name}, sent to {ProviderOf(view)}");
            AutomationProperties.SetName(SameForBoth, $"{view.Name}: use the archive password as the site password too");
            if (!view.CanTakeLinkPassword)
            {
                HasPassword.IsChecked = false;
                ClearPasswords();
                ShowPasswordBoxes();
            }

            _extras.Visibility = view.CanRetry || view.CanChooseDirect || view.CanTakeLinkPassword ? Visibility.Visible : Visibility.Collapsed;
        }

        private string ProviderOf(AddLineView view) => string.IsNullOrWhiteSpace(_line.Result?.ProviderName) ? _provider : _line.Result.ProviderName;
    }
}
