using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T3 in Progress Details (docs/plans/torrent-mockups/c-torrent-details.html): file priorities on the Files tab, the
/// torrent's own Options tab (limits, seeding goal, order) in place of the speed limit, Force recheck and Announce now,
/// the trackers' next announce, the Overview's facts, and the watch that makes the Host report progress every second
/// while this window shows a running torrent. All of it only for a Host that answers the T3 requests
/// (<see cref="HostConnection.ProbeTorrentControlsAsync"/>); an older one shows the window as before.
/// </summary>
public sealed partial class DownloadWindow
{
    /// <summary>T3: the files' priorities that aren't Normal, by index: the Host's, or the user's while the torrent waits for its choice.</summary>
    private Dictionary<int, string> _priorities = [];
    private TorrentWatch? _watch;
    private bool _controlBusy;
    private bool _optionsLoaded;
    private bool _optionsBusy;
    private bool _showingOptions;
    /// <summary>A read of the options was tried since the tab opened or the Host connected; a failed one isn't repeated on its own.</summary>
    private bool _optionsTried;
    /// <summary>The torrent's v2 info hash once a details read gave it; the progress updates don't carry it.</summary>
    private string? _infoHashV2;
    private long _detailsReadAt = long.MinValue / 2;
    private bool _detailsReading;
    private (string State, bool Seeding)? _detailsKey;

    /// <summary>The Host has the T3 torrent controls.</summary>
    private bool Controls => TorrentControlRules.Supported(TorrentsOffered, _connection.TorrentControls);

    private void InitializeTorrentControls()
    {
        _watch = new TorrentWatch(DispatcherQueue, on => _connection.WatchTorrentsAsync(on));
        Closed += (_, _) => _watch?.Stop();
        _connection.ConnectionChanged += (connected, reason) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _watch?.Reset();
            // The options must be read again from the Host before Apply can send them.
            _optionsLoaded = false;
            _optionsTried = false;
            TorrentOptionsApply.IsEnabled = false;
            if (!connected) return;
            _detailsKey = null;
            _probing = true;
            _ = ProbeAsync();
        });
    }

    /// <summary>Asked once per connection (the connection keeps the answer); set while one is out or was asked.</summary>
    private bool _probing;

    private async Task ProbeAsync()
    {
        await _connection.ProbeTorrentControlsAsync();
        if (_closed) return;
        if (_job is { } job) ShowTorrentControls(job);
        UpdateButtons();
    }

    /// <summary>The T3 parts of the window, as the job and the Host allow. Runs on each update.</summary>
    private void ShowTorrentControls(JobDto job)
    {
        var torrent = job.Torrent is not null;
        var controls = torrent && Controls;
        if (torrent && _connected && !_probing && _connection.TorrentControls is null)
        {
            _probing = true;
            _ = ProbeAsync();
        }

        // The torrent's own options replace the speed limit; a finished torrent keeps them for seeding.
        TorrentOptionsCard.Visibility = controls ? Visibility.Visible : Visibility.Collapsed;
        SpeedLimitCard.Visibility = torrent ? Visibility.Collapsed : Visibility.Visible;
        CompletionCard.Visibility = job.State == "completed" ? Visibility.Collapsed : Visibility.Visible;
        if (_completedLayout && torrent)
        {
            OptionsTab.Visibility = controls ? Visibility.Visible : Visibility.Collapsed;
            if (!controls && Tabs.SelectedItem == OptionsTab) Tabs.SelectedItem = OverviewTab;
        }

        FilesPriorityHint.Visibility = controls && !FilesFixed(job) ? Visibility.Visible : Visibility.Collapsed;
        PriorityColumn.Width = new GridLength(controls ? 64 : 0);
        NextAnnounceColumn.Width = new GridLength(controls ? 96 : 0);
        ShowFilePriorities();
        LoadOptionsIfWanted();
        ReadDetailsIfDue(job);
        SyncWatch();
    }

    // ---- Watching ----

    /// <summary>Watches while this window shows, isn't minimized, and its torrent downloads or seeds.</summary>
    private void SyncWatch()
        => _watch?.Sync(!_closed && TorrentControlRules.Watches(AppWindow.IsVisible, Minimized, _connected, Controls, [_job]));

    // ---- Force recheck and Announce now ----

    private void ShowControlButtons()
    {
        var controls = _job?.Torrent is not null && Controls;
        RecheckButton.Visibility = controls && TorrentControlRules.CanRecheck(_job) ? Visibility.Visible : Visibility.Collapsed;
        AnnounceButton.Visibility = controls && TrackersPage.Visibility == Visibility.Visible && TorrentControlRules.CanAnnounce(_job)
            ? Visibility.Visible : Visibility.Collapsed;
        RecheckButton.IsEnabled = AnnounceButton.IsEnabled = _connected && !_controlBusy;
        // Announce shows only on the Trackers tab, so the footer is laid out again whenever these change.
        LayOutFooter();
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await RunControlAsync(recheck: true);

    private async void Announce_Click(object sender, RoutedEventArgs e) => await RunControlAsync(recheck: false);

    private async Task RunControlAsync(bool recheck)
    {
        if (_controlBusy || _job is not { } job || !Controls) return;
        _controlBusy = true;
        ShowControlButtons();
        try
        {
            var result = recheck ? await _connection.RecheckTorrentAsync(_jobId) : await _connection.AnnounceTorrentAsync(_jobId);
            if (_closed) return;
            var name = !string.IsNullOrWhiteSpace(job.FileName) ? job.FileName : job.Torrent?.Name;
            Notice.Text = result.Ok
                ? TorrentControlRules.Said(recheck, name, 1, 0, null)
                : TorrentControlRules.Said(recheck, name, 0, 1, string.IsNullOrWhiteSpace(result.Error) ? null : RemoteText.Failure(result.Error));
            // The trackers' next announce changes at once.
            if (!recheck && result.Ok) _torrentQueriedAt = null;
        }
        catch (Exception ex)
        {
            if (!_closed) Notice.Text = recheck ? $"The check didn't start. {ex.Message}" : $"The announce didn't start. {ex.Message}";
        }
        finally
        {
            _controlBusy = false;
            if (!_closed) ShowControlButtons();
        }
    }

    // ---- The Overview's facts (torrent.details) ----

    /// <summary>
    /// Read when the Overview shows: once, again when the torrent's state changes, and while it runs at most every
    /// <see cref="TorrentControlRules.DetailsReadMs"/> on its own progress updates (no timer).
    /// </summary>
    private void ReadDetailsIfDue(JobDto job)
    {
        if (_detailsReading || !_connected || job.Torrent is null || !Controls || OverviewPage.Visibility != Visibility.Visible) return;
        var key = TorrentDetailsRules.RunKey(job);
        var due = _detailsKey != key
            || TorrentControlRules.DetailsDue(true, TorrentDetailsRules.MayBeRunning(job), Environment.TickCount64 - _detailsReadAt);
        if (!due) return;
        _detailsKey = key;
        _ = ReadDetailsAsync();
    }

    private async Task ReadDetailsAsync()
    {
        _detailsReading = true;
        _detailsReadAt = Environment.TickCount64;
        try
        {
            var details = await _connection.GetTorrentDetailsAsync(_jobId);
            if (_closed || details is not { Ok: true }) return;
            ShowFact(AddedHeader, AddedText, TorrentControlRules.AddedText(details.AddedAt, details.Source));
            _infoHashV2 = TorrentControlRules.KeptV2(_infoHashV2, details.InfoHashV2);
            InfoHashText.Text = TorrentControlRules.HashText(_job?.Torrent?.InfoHash ?? details.InfoHash, _infoHashV2);
            ShowFact(PiecesHeader, PiecesFactText, TorrentControlRules.PiecesText(details.PieceCount, details.PieceLength));
            ShowFact(WastedHeader, WastedText, details.Running ? TorrentControlRules.WastedText(details.Wasted, details.HashFailures) : string.Empty);
            ShowFact(PrivateHeader, PrivateText, details.Private is null ? string.Empty : TorrentControlRules.PrivateText(details.Private));
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Torrent details not read: {ex.GetType().Name}");
        }
        finally
        {
            _detailsReading = false;
        }
    }

    /// <summary>A fact keeps what it last said while the torrent isn't running; an empty one stays hidden.</summary>
    private static void ShowFact(TextBlock header, TextBlock value, string text)
    {
        if (text.Length == 0) return;
        value.Text = text;
        header.Visibility = value.Visibility = Visibility.Visible;
    }

    // ---- File priorities on the Files tab ----

    private void ShowFilePriorities()
    {
        var shown = _job?.Torrent is not null && Controls;
        foreach (var row in _fileRows) row.SetPriority(_priorities.GetValueOrDefault(row.Index, TorrentPriorities.Normal), shown);
    }

    /// <summary>Right-click, Shift+F10 or the menu key on a file: High, Normal, Low or Skip.</summary>
    private void FilesList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (!Controls || _filesReadOnly || _job?.Torrent is null) return;
        var element = args.OriginalSource as FrameworkElement;
        var row = element?.DataContext as TorrentFileRow ?? (element as ContentControl)?.Content as TorrentFileRow;
        if (row is null || element is null || !row.CanCheck) return;
        args.Handled = true;
        var current = row.Priority;
        var menu = new MenuFlyout();
        foreach (var priority in TorrentPriorities.Menu)
        {
            var name = TorrentPriorities.Name(priority);
            var item = new ToggleMenuFlyoutItem { Text = name, IsChecked = name == current };
            item.Click += async (_, _) =>
            {
                try
                {
                    await SetFilePriorityAsync(row, priority);
                }
                catch (Exception ex)
                {
                    if (!_closed) ShowFilesNote(ex.Message);
                }
            };
            menu.Items.Add(item);
        }

        if (args.TryGetPosition(element, out var point)) menu.ShowAt(element, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
    }

    /// <summary>
    /// One file's priority. Skip is the same as clearing its box; any other ticks it. While the torrent waits for its
    /// choice it stays here until Start; otherwise it goes to the Host at once (a running torrent takes it then).
    /// </summary>
    private async Task SetFilePriorityAsync(TorrentFileRow row, string priority)
    {
        if (priority == TorrentPriorities.Skip)
        {
            if (row.Checked) await ToggleFileAsync(row, false);
            return;
        }

        if (_choosing)
        {
            if (priority == TorrentPriorities.Normal) _priorities.Remove(row.Index);
            else _priorities[row.Index] = priority;
            if (!row.Checked) await ToggleFileAsync(row, true);
            ShowFilePriorities();
            return;
        }

        if (!_connected)
        {
            ShowFilesNote("The Host is not connected.");
            return;
        }

        if (_savingFiles)
        {
            ShowFilesNote("The last change is still being saved.");
            return;
        }

        _savingFiles = true;
        _choiceVersion++;
        UpdateButtons();
        try
        {
            var result = await _connection.SetTorrentPrioritiesAsync(_jobId, [new TorrentFilePriorityDto { Index = row.Index, Priority = priority }]);
            if (_closed) return;
            if (result.Ok)
            {
                _wanted.Add(row.Index);
                if (priority == TorrentPriorities.Normal) _priorities.Remove(row.Index);
                else _priorities[row.Index] = priority;
                row.ShowChecked(true);
                ShowFilePriorities();
                ClearFilesNote();
                ReloadFiles();
            }
            else
            {
                ShowFilesNote(result.Error ?? "The Host did not change the priority.");
            }
        }
        finally
        {
            _savingFiles = false;
            if (!_closed) UpdateButtons();
        }
    }

    // ---- The torrent's own Options tab ----

    /// <summary>The tab opened: its options are read from the Host (again after a reconnect or a failed read).</summary>
    private void OptionsTabShown(bool shown)
    {
        var opened = shown && !_showingOptions;
        _showingOptions = shown;
        if (opened) _optionsTried = false;
        LoadOptionsIfWanted();
        if (shown) TorrentGoalText();
    }

    /// <summary>
    /// Reads the options while the tab shows and they aren't loaded: when it opens, and when the Host's answer about
    /// the controls or a new connection comes while it is open. A failed read waits for the tab to open again.
    /// </summary>
    private void LoadOptionsIfWanted()
    {
        TorrentOptionsApply.IsEnabled = _connected && _optionsLoaded && !_optionsBusy;
        if (TorrentControlRules.OptionsReadDue(_showingOptions, _job?.Torrent is not null && Controls, _connected, _optionsLoaded, _optionsBusy, _optionsTried))
            _ = LoadOptionsAsync();
    }

    /// <summary>"Global goal (ratio 1.00 or 48 h)", from Options › Torrents as the Host has it now.</summary>
    private void TorrentGoalText()
    {
        var text = TorrentControlRules.GlobalGoalText(_settings);
        if (TorrentGoal.Items.Count == 0 || Equals(TorrentGoal.Items[0], text)) return;
        var selected = TorrentGoal.SelectedIndex;
        TorrentGoal.Items[0] = text;
        TorrentGoal.SelectedIndex = selected;
    }

    private async Task LoadOptionsAsync()
    {
        if (_optionsBusy || !_connected) return;
        _optionsBusy = true;
        _optionsTried = true;
        TorrentOptionsApply.IsEnabled = false;
        try
        {
            var result = await _connection.GetTorrentOptionsAsync(_jobId);
            if (_closed) return;
            if (result is not { Ok: true, Options: { } options })
            {
                TorrentOptionsNote.Text = result is null ? TorrentControlRules.OlderHost
                    : string.IsNullOrWhiteSpace(result.Error) ? "The options couldn't be read." : RemoteText.Failure(result.Error);
                return;
            }

            _optionsLoaded = true;
            // Audit L5: a read after a reconnect leaves edits not yet applied as they are.
            if (TorrentControlRules.ReadFillsForm(_optionsFilled, FormShown())) ShowOptions(TorrentControlRules.FormOf(options));
            else TorrentOptionsNote.Text = TorrentControlRules.EditsKept;
        }
        catch (Exception ex)
        {
            if (!_closed) TorrentOptionsNote.Text = $"The options couldn't be read. {ex.Message}";
        }
        finally
        {
            _optionsBusy = false;
            if (!_closed) TorrentOptionsApply.IsEnabled = _connected && _optionsLoaded;
        }
    }

    private void ShowOptions(TorrentControlRules.OptionsForm form)
    {
        TorrentDownloadLimited.IsChecked = !double.IsNaN(form.DownloadKilobytes);
        TorrentDownloadLimit.Value = form.DownloadKilobytes;
        TorrentUploadLimited.IsChecked = !double.IsNaN(form.UploadKilobytes);
        TorrentUploadLimit.Value = form.UploadKilobytes;
        TorrentGoalText();
        TorrentGoal.SelectedIndex = Math.Max(0, TorrentControlRules.Goals.ToList().IndexOf(form.Goal));
        TorrentGoalValue.Value = form.GoalValue;
        TorrentSequential.IsChecked = form.Sequential;
        FollowLimits();
        FollowGoal();
        _optionsFilled = FormShown();
    }

    /// <summary>The form as the last read or Apply filled it; null before the first.</summary>
    private TorrentControlRules.OptionsForm? _optionsFilled;

    private void TorrentLimit_Click(object sender, RoutedEventArgs e) => FollowLimits();

    private void FollowLimits()
    {
        TorrentDownloadLimit.IsEnabled = TorrentDownloadLimited.IsChecked == true;
        TorrentUploadLimit.IsEnabled = TorrentUploadLimited.IsChecked == true;
    }

    private void TorrentGoal_SelectionChanged(object sender, SelectionChangedEventArgs e) => FollowGoal();

    /// <summary>The value box shows for Ratio and Time only, with its unit and range.</summary>
    private void FollowGoal()
    {
        var goal = TorrentGoal.SelectedIndex is >= 0 and < 4 ? TorrentControlRules.Goals[TorrentGoal.SelectedIndex] : "global";
        var ratio = goal == "ratio";
        TorrentGoalValueRow.Visibility = goal is "ratio" or "hours" ? Visibility.Visible : Visibility.Collapsed;
        TorrentGoalUnit.Text = ratio ? "ratio (0.01 to 100)" : "hours (0.1 to 8760)";
        TorrentGoalValue.SmallChange = ratio ? 0.1 : 1;
        TorrentGoalValue.LargeChange = ratio ? 1 : 24;
        AutomationProperties.SetName(TorrentGoalValue, ratio ? "Seeding ratio" : "Seeding hours");
    }

    /// <summary>What the tab shows now; a limit whose box isn't ticked is none.</summary>
    private TorrentControlRules.OptionsForm FormShown()
    {
        var goal = TorrentGoal.SelectedIndex is >= 0 and < 4 ? TorrentControlRules.Goals[TorrentGoal.SelectedIndex] : "global";
        return new TorrentControlRules.OptionsForm(
            TorrentDownloadLimited.IsChecked == true ? TorrentDownloadLimit.Value : double.NaN,
            TorrentUploadLimited.IsChecked == true ? TorrentUploadLimit.Value : double.NaN,
            goal,
            TorrentGoalValue.Value,
            TorrentSequential.IsChecked == true);
    }

    private async void TorrentOptionsApply_Click(object sender, RoutedEventArgs e)
    {
        if (_optionsBusy || !_optionsLoaded || !_connected || !Controls) return;
        var form = FormShown();
        // A ticked limit needs a number.
        if ((TorrentDownloadLimited.IsChecked == true && double.IsNaN(form.DownloadKilobytes))
            || (TorrentUploadLimited.IsChecked == true && double.IsNaN(form.UploadKilobytes)))
        {
            TorrentOptionsNote.Text = "Enter a limit from 1 to 2,097,151 KB/s, or clear its box for none.";
            return;
        }

        if (TorrentControlRules.Problem(form) is { } problem)
        {
            TorrentOptionsNote.Text = problem;
            return;
        }

        _optionsBusy = true;
        TorrentOptionsApply.IsEnabled = false;
        try
        {
            var result = await _connection.SetTorrentOptionsAsync(TorrentControlRules.ToDto(_jobId, form));
            if (_closed) return;
            if (result is { Ok: true })
            {
                if (result.Options is { } saved) ShowOptions(TorrentControlRules.FormOf(saved));
                // Saved even when the Host adds a word (an older torrent program doesn't take the download order).
                TorrentOptionsNote.Text = TorrentControlRules.SavedText(result.Running, result.Error);
            }
            else
            {
                TorrentOptionsNote.Text = string.IsNullOrWhiteSpace(result.Error) ? "The Host didn't take the options." : RemoteText.Failure(result.Error);
            }
        }
        catch (Exception ex)
        {
            if (!_closed) TorrentOptionsNote.Text = $"The options weren't saved. {ex.Message}";
        }
        finally
        {
            _optionsBusy = false;
            if (!_closed) TorrentOptionsApply.IsEnabled = _connected && _optionsLoaded;
        }
    }
}
