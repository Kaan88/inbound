using System.Globalization;
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// What is known about one download: its figures, its segment map and its segment table. A file's own
/// window and a package's window both show this, so a file reads the same wherever it is looked at.
/// </summary>
public sealed partial class JobDetailsPanel : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private HostConnection? _connection;
    private SegmentMapBar? _map;
    private readonly List<SegmentMapBar> _extraMaps = [];
    private ProgressBar? _movingBar;
    private FrameworkElement _root;
    private JobDto? _job;
    private bool _connected;
    private bool _stopped;
    private bool _loadingSegments;
    private bool _haveSegmentMap;

    // Highest completed byte count shown per segment start; display never moves backwards between refreshes.
    private readonly Dictionary<long, long> _segmentHighWater = [];

    // Exponentially smoothed per-segment speed; raw per-stream rates sawtooth between reads.
    private readonly Dictionary<long, double> _segmentRate = [];
    private readonly List<SegmentRow> _segmentRows = [];

    // Built here and placed once: in the expander, or straight in the panel for a window that gives the table a tab.
    private readonly StackPanel Segments = new() { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };

    private sealed record SegmentRow(TextBlock Number, TextBlock Range, ProgressBar Bar, TextBlock Downloaded, TextBlock Speed, TextBlock Info);

    public JobDetailsPanel()
    {
        InitializeComponent();
        _root = this;
        _timer.Tick += async (_, _) => await LoadSegmentsAsync();
        Loaded += (_, _) => { if (Segments.Parent is null) ExpanderBody.Children.Add(Segments); };
        SegmentDetails.Expanding += (_, _) => SegmentsLayoutChanged?.Invoke();
        SegmentDetails.Collapsed += (_, _) => SegmentsLayoutChanged?.Invoke();
    }

    /// <summary>The segment table grew, shrank, opened or closed.</summary>
    internal event Action? SegmentsLayoutChanged;

    /// <summary>The segment details couldn't be read, in words for the window's notice line.</summary>
    internal event Action<string>? Problem;

    internal bool SegmentsOpen => SegmentDetails.IsExpanded;

    internal int SegmentRowCount => _segmentRows.Count;

    /// <summary>Whether to say where the file goes. A file's own window has a tab for that.</summary>
    internal bool ShowSavePath
    {
        set => SavePathLabel.Visibility = SavePathValue.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The table alone, always open, without the figures above it: a window that shows the figures its own way
    /// (the progress window's Overview tab) and gives the table a tab of its own.
    /// </summary>
    internal void UseTableOnly() => UseOpenTable(showFigures: false);

    /// <summary>
    /// The connections table always open under the figures, with no "Segment details" expander: the package window's
    /// selected file, shown as Progress Details shows its connections.
    /// </summary>
    internal void UseOpenTable(bool showFigures = true)
    {
        if (Segments.Parent is not null) return;
        Figures.Visibility = showFigures ? Visibility.Visible : Visibility.Collapsed;
        SegmentDetails.Visibility = Visibility.Collapsed;
        Body.Children.Add(Segments);
    }

    /// <summary>Another segment map in the window that shows the same as the main one.</summary>
    internal void AddMap(SegmentMapBar map) => _extraMaps.Add(map);

    private IEnumerable<SegmentMapBar> Maps => _map is null ? _extraMaps : _extraMaps.Prepend(_map);

    /// <summary>Starts reading segments over the window's connection.</summary>
    /// <param name="map">The window's segment map, which may sit anywhere in it.</param>
    /// <param name="movingBar">The window's bar for a download of unknown size. The two share a place, and only one shows.</param>
    /// <param name="root">The window's root, for theme colors.</param>
    internal void Attach(HostConnection connection, SegmentMapBar map, ProgressBar movingBar, FrameworkElement root)
    {
        _connection = connection;
        _map = map;
        _movingBar = movingBar;
        _root = root;
        UpdatePolling(readOnce: true);
    }

    internal void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        UpdatePolling(readOnce: connected);
    }

    /// <summary>False while the window is hidden or minimised: nothing is read until it is shown again.</summary>
    internal void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        UpdatePolling(readOnce: shown);
    }

    internal void Stop()
    {
        _stopped = true;
        _timer.Stop();
    }

    private bool _shown = true;

    /// <summary>
    /// Audit M11: <c>job.segments</c> and <c>job.connections</c> are read every second only while the download can move
    /// (connecting, downloading, verifying, waiting to retry) and the window is shown. A paused or queued download's
    /// saved ranges are read once (when shown, connected, or after it stops moving), not every second.
    /// </summary>
    private void UpdatePolling(bool readOnce = false)
    {
        // T1: a torrent has pieces from many peers, not byte ranges on connections: never read.
        var canRead = !_stopped && _connection is not null && _connected && _shown && _job is { IsTerminal: false, Torrent: null };
        var poll = canRead && SpeedSample.IsActive(_job!.State);
        if (poll)
        {
            if (!_timer.IsEnabled) _timer.Start();
            return;
        }
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            readOnce = true;
        }
        if (readOnce && canRead) _ = LoadSegmentsAsync();
    }

    internal void Show(JobDto job)
    {
        var another = _job?.Id != job.Id;
        if (another) ForgetSegments();
        _job = job;
        UpdatePolling(readOnce: another);
        var complete = job.State == "completed";
        var percent = Percent(job);
        // T1: a torrent has pieces from many peers, not byte ranges on connections. No map, no table, no polling (UpdatePolling).
        var torrent = job.Torrent is not null;
        var moving = ProgressBarRules.ShowsMovingBar(job.State, job.TotalBytes);
        if (_movingBar is { } bar)
        {
            bar.IsIndeterminate = moving && !torrent;
            bar.Value = complete ? 100 : percent;
            bar.Visibility = moving && !torrent ? Visibility.Visible : Visibility.Collapsed;
        }

        // One main bar: the segment map when the size is known, the indeterminate bar only for unknown sizes.
        foreach (var map in Maps)
        {
            map.Visibility = !torrent && (job.TotalBytes is > 0 || complete) ? Visibility.Visible : Visibility.Collapsed;
            if (complete) map.SetFraction(1);
            else if (!_haveSegmentMap) map.SetFraction(percent / 100);
        }

        StatusValue.Text = TorrentRules.StatusText(job) ?? JobNotes.NotStartedText(job) ?? JobViewModel.ToDisplayState(job.State);
        SizeValue.Text = job.TotalBytes is >= 0 ? Bytes(job.TotalBytes.Value) : "Unknown";
        DownloadedValue.Text = job.TotalBytes is > 0
            ? $"{Bytes(job.ReceivedBytes)} ({percent:0.00}%)"
            : Bytes(job.ReceivedBytes);
        RateValue.Text = job.BytesPerSecond is > 0 ? $"{Bytes((long)job.BytesPerSecond.Value)}/s" : "—";
        TimeLeftValue.Text = job.EstimatedRemaining is { } eta && !complete
            ? FormatDuration(eta)
            : complete ? "Complete" : "—";
        ResumeValue.Text = torrent ? "—" : job.ResumeSupported switch
        {
            true => "Yes",
            false => "No",
            null => "Unknown",
        };
        SavePathValue.Text = job.FinalFilePath;

        if ((job.IsTerminal || torrent) && Segments.Children.Count > 0)
        {
            Segments.Children.Clear();
            _segmentRows.Clear();
            SegmentsLayoutChanged?.Invoke();
        }
    }

    /// <summary>There is no download to talk about, in these words.</summary>
    internal void ShowNothing(string status)
    {
        ForgetSegments();
        _job = null;
        UpdatePolling();
        StatusValue.Text = status;
        SizeValue.Text = "Unknown";
        DownloadedValue.Text = Bytes(0);
        RateValue.Text = "—";
        TimeLeftValue.Text = "—";
        ResumeValue.Text = "Unknown";
        SavePathValue.Text = string.Empty;
        if (_movingBar is { } bar)
        {
            bar.IsIndeterminate = false;
            bar.Value = 0;
        }

        foreach (var map in Maps)
        {
            map.SetFraction(0);
            map.Visibility = Visibility.Collapsed;
        }
    }

    private void ForgetSegments()
    {
        _haveSegmentMap = false;
        _segmentHighWater.Clear();
        _segmentRate.Clear();
        _segmentRows.Clear();
        if (Segments.Children.Count > 0)
        {
            Segments.Children.Clear();
            SegmentsLayoutChanged?.Invoke();
        }
    }

    private async Task LoadSegmentsAsync()
    {
        if (_stopped || _loadingSegments || !_connected || _connection is null || _job is null || _job.IsTerminal || _job.Torrent is not null) return;
        var jobId = _job.Id;
        bool StillShowing() => !_stopped && _job is { IsTerminal: false, Torrent: null } shown && shown.Id == jobId;
        _loadingSegments = true;
        try
        {
            var reply = await _connection.GetSegmentsAsync(jobId);
            if (!StillShowing()) return;
            var segments = reply.GetPayload<List<SegmentDto>>();
            var connectionsReply = await _connection.GetConnectionsAsync(jobId);
            if (!StillShowing()) return;
            var connections = connectionsReply.GetPayload<List<ConnectionTelemetryDto>>();
            var job = _job!;
            var total = job.TotalBytes ?? 0;

            // Single-stream downloads have no saved ranges; show them as one segment covering the file.
            if (segments.Count == 0 && total > 0)
            {
                segments = [new SegmentDto { Start = 0, End = total - 1, Completed = job.ReceivedBytes,
                    IsComplete = job.ReceivedBytes >= total, Fraction = job.ReceivedBytes / (double)total }];
            }

            // Checkpoints lag the live streams. Each live range starts at segment.Start + (bytes already saved),
            // so its true position is RangeStart - Start + ReceivedBytes. Take the furthest known point.
            foreach (var segment in segments)
            {
                var live = connections
                    .Where(c => c.RangeStart is { } from && from >= segment.Start && from <= segment.End)
                    .Select(c => c.RangeStart!.Value - segment.Start + c.ReceivedBytes)
                    .DefaultIfEmpty(0).Max();
                var length = Math.Max(1, segment.End - segment.Start + 1);
                _segmentHighWater.TryGetValue(segment.Start, out var shown);
                segment.Completed = Math.Min(length, Math.Max(Math.Max(segment.Completed, live), shown));
                _segmentHighWater[segment.Start] = segment.Completed;
                segment.IsComplete |= segment.Completed >= length;
            }

            if (segments.Count > 0)
            {
                foreach (var map in Maps) map.SetSegments(segments, total);
                _haveSegmentMap = true;
            }
            else
            {
                foreach (var map in Maps) map.SetFraction(0);
            }

            RenderSegmentTable(segments, connections);
        }
        catch (Exception ex)
        {
            if (!_stopped) Problem?.Invoke($"Saved segment details unavailable: {ex.Message}");
        }
        finally
        {
            _loadingSegments = false;
        }
    }

    private void RenderSegmentTable(IReadOnlyList<SegmentDto> segments, IReadOnlyList<ConnectionTelemetryDto> connections)
    {
        var secondary = InboundTheme.Brush("InboundSecondaryTextBrush", _root);
        void Tone(TextBlock block, bool muted)
        {
            // Normal cells keep the inherited theme foreground; a null brush renders invisible text.
            if (muted) block.Foreground = secondary;
            else block.ClearValue(TextBlock.ForegroundProperty);
        }

        if (segments.Count == 0)
        {
            if (_segmentRows.Count > 0 || Segments.Children.Count != 1)
            {
                _segmentRows.Clear();
                Segments.Children.Clear();
                Segments.Children.Add(SegmentText("No segment information yet.", secondary, muted: true));
                SegmentsLayoutChanged?.Invoke();
            }

            return;
        }

        // Rows are updated in place. Recreating the ProgressBars on every refresh made each bar
        // first draw against a default width and then snap to the real one, so the bars jumped.
        var paused = _job?.State == "paused";
        if (_segmentRows.Count != segments.Count || Segments.Children.Count != segments.Count + 2)
        {
            BuildSegmentRows(segments.Count, secondary);
            SegmentsLayoutChanged?.Invoke();
        }

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var length = Math.Max(1, segment.End - segment.Start + 1);
            var live = connections.Where(c => c.RangeStart is { } from ? from >= segment.Start && from <= segment.End
                : segments.Count == 1).ToList();
            var raw = live.Sum(c => Math.Max(0, c.BytesPerSecond));
            var rate = _segmentRate.TryGetValue(segment.Start, out var previous) && raw > 0 ? previous * 0.75 + raw * 0.25 : raw;
            _segmentRate[segment.Start] = rate;
            var row = _segmentRows[index];
            var value = Math.Clamp(segment.Completed * 100.0 / length, 0, 100);
            if (Math.Abs(row.Bar.Value - value) > 0.001) row.Bar.Value = value;
            row.Downloaded.Text = $"{Bytes(segment.Completed)} / {Bytes(length)}";
            row.Range.Text = $"{Bytes(segment.Start)} – {Bytes(segment.End + 1)}";
            ToolTipService.SetToolTip(row.Range, $"Bytes {segment.Start:N0} to {segment.End:N0}");
            row.Speed.Text = rate > 0 ? $"{Bytes((long)rate)}/s" : "—";
            Tone(row.Speed, rate <= 0);
            // Only what the Host reports: a connection on the range, the range done, or neither.
            // It doesn't say which connection is retrying; the download's own retries show in the window.
            row.Info.Text = segment.IsComplete ? "Done" : live.Count > 0 ? "Receiving" : paused ? "Paused" : "Waiting";
            row.Info.Foreground = InboundTheme.Brush(segment.IsComplete ? "InboundStatusDoneBrush"
                : live.Count > 0 ? "InboundStatusActiveBrush" : paused ? "InboundStatusWaitingBrush" : "InboundSecondaryTextBrush", _root);
        }
    }

    private static TextBlock SegmentText(string value, Microsoft.UI.Xaml.Media.Brush secondary, bool muted = false, bool header = false)
    {
        var block = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = header ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };
        if (muted || header) block.Foreground = secondary;
        return block;
    }

    private void BuildSegmentRows(int count, Microsoft.UI.Xaml.Media.Brush secondary)
    {
        _segmentRows.Clear();
        Segments.Children.Clear();
        static Grid NewRow()
        {
            var row = new Grid { ColumnSpacing = 10, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var width in new[] { 24d, 136d, -1d, 122d, 74d, 66d })
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
            }

            return row;
        }

        static T Cell<T>(Grid row, int column, T element) where T : FrameworkElement
        {
            Grid.SetColumn(element, column);
            element.VerticalAlignment = VerticalAlignment.Center;
            // UI.R6: Downloaded and Speed are live counters in fixed columns. Right-aligned, header and rows alike,
            // their units hold still as the digit count changes.
            if (column is 3 or 4 && element is TextBlock counter)
            {
                counter.HorizontalAlignment = HorizontalAlignment.Right;
                counter.TextAlignment = TextAlignment.Right;
            }

            row.Children.Add(element);
            return element;
        }

        var head = NewRow();
        Cell(head, 0, SegmentText("N.", secondary, header: true));
        Cell(head, 1, SegmentText("Range", secondary, header: true));
        Cell(head, 2, SegmentText("Progress", secondary, header: true));
        Cell(head, 3, SegmentText("Downloaded", secondary, header: true));
        Cell(head, 4, SegmentText("Speed", secondary, header: true));
        Cell(head, 5, SegmentText("State", secondary, header: true));
        Segments.Children.Add(head);
        Segments.Children.Add(new Border { Height = 1, Background = InboundTheme.Brush("InboundDividerBrush", _root) });
        for (var index = 0; index < count; index++)
        {
            var row = NewRow();
            _segmentRows.Add(new SegmentRow(
                Cell(row, 0, SegmentText((index + 1).ToString(CultureInfo.CurrentCulture), secondary, muted: true)),
                Cell(row, 1, SegmentText(string.Empty, secondary, muted: true)),
                Cell(row, 2, InboundTheme.Styled(new ProgressBar { Maximum = 100 })),
                Cell(row, 3, SegmentText(string.Empty, secondary)),
                Cell(row, 4, SegmentText("—", secondary, muted: true)),
                Cell(row, 5, SegmentText(string.Empty, secondary, muted: true))));
            Segments.Children.Add(row);
        }
    }

    internal static double Percent(JobDto job) => job.TotalBytes is > 0
        ? Math.Clamp(job.ReceivedBytes * 100.0 / job.TotalBytes.Value, 0, 100)
        : job.Progress is { } progress ? Math.Clamp(progress * 100, 0, 100) : 0;

    internal static string Bytes(long bytes) => UiFormat.Bytes(bytes);

    /// <summary>U-WL-15: the list's own words for time left, such as 12m 4s.</summary>
    private static string FormatDuration(TimeSpan duration)
        => DurationText.TimeLeft(duration) is { Length: > 0 } words ? words : "—";
}
