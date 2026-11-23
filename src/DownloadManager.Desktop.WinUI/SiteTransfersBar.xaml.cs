using System.Collections.ObjectModel;
using System.ComponentModel;
using DownloadManager.Contracts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using static DownloadManager.Desktop.WinUI.SiteTransferRules;

namespace DownloadManager.Desktop.WinUI;

/// <summary>The strip above the opened Transfers list that is dragged to change its height.</summary>
public sealed partial class HeightGrip : Grid
{
    public HeightGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}

internal enum TransferRowKind { Batch, File, More, Note }

/// <summary>
/// One row of the Transfers list: a batch, one of its files, or the "Show more" / "Reading the files" line after them.
/// Public because the row's bindings read it by reflection; updated in place, so a row keeps its focus and selection while
/// its figures change each second. Its text comes from <see cref="SiteTransferRules"/>.
/// </summary>
public sealed class SiteTransferRow : INotifyPropertyChanged
{
    private string _stamp = "";

    /// <summary>A file sits under its batch's arrow: the batch's chevron (22) and the column gap (2).</summary>
    private const double FileIndent = 24;

    internal SiteTransferRow(string key) => Key = key;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }
    internal TransferRowKind Kind { get; private set; }
    internal TransferRef? Ref { get; private set; }
    internal string BatchId { get; private set; } = "";
    internal bool IsExpanded { get; private set; }

    public string Name { get; private set; } = "";
    public Thickness Indent { get; private set; }
    public string ChevronGlyph { get; private set; } = "";
    public string ChevronName { get; private set; } = "";
    public Visibility ChevronVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility DownArrowVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility UpArrowVisibility { get; private set; } = Visibility.Collapsed;
    public string SizeText { get; private set; } = "";
    public string StatusText { get; private set; } = "";
    public Brush StatusBrush { get; private set; } = StatusBrushes.Neutral;
    public double Percent { get; private set; }
    public Visibility DownBarVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility UpBarVisibility { get; private set; } = Visibility.Collapsed;
    public string EtaText { get; private set; } = "";
    public string RateText { get; private set; } = "";
    public string Description { get; private set; } = "";
    /// <summary>Null on a note row: an empty string would still show an empty tooltip.</summary>
    public string? ToolTip { get; private set; }
    public string AccessibleName { get; private set; } = "";

    /// <summary>
    /// SITES.10 (6): the words of the button on a batch stopped by a sign-in, certificate or key problem, as a listing
    /// names it ("Sign in", "Check the certificate"); empty and hidden on every other row.
    /// </summary>
    public string FixText { get; private set; } = "";

    public Visibility FixVisibility { get; private set; } = Visibility.Collapsed;

    private static Brush BrushOf(StatusKind kind) => kind switch
    {
        StatusKind.Active => StatusBrushes.Active,
        StatusKind.Done => StatusBrushes.Done,
        StatusKind.Failed => StatusBrushes.Failed,
        StatusKind.Waiting => StatusBrushes.Waiting,
        _ => StatusBrushes.Neutral,
    };

    internal void UpdateBatch(SiteTransferDto t, bool expanded)
    {
        Kind = TransferRowKind.Batch;
        Ref = new TransferRef(t);
        BatchId = t.Id;
        IsExpanded = expanded;
        var status = Status(t);
        Name = Title(t);
        Indent = new Thickness(0);
        var hasFiles = t.Files > 1 || t.Folders > 0;
        ChevronVisibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ChevronGlyph = expanded ? "" : "";
        ChevronName = expanded ? "Hide the files" : "Show the files";
        SetDirection(IsDownload(t), status.Percent);
        SizeText = SiteTransferRules.SizeText(t);
        StatusText = status.Text;
        StatusBrush = BrushOf(status.Kind);
        Percent = status.Percent ?? 0;
        EtaText = TimeLeft(t);
        RateText = SiteTransferRules.RateText(t);
        Description = SiteTransferRules.Description(t);
        ToolTip = $"{Name}\n{FullRoute(t)}\n{Description}";
        SetFix(SiteTransferRules.FixableProblem(t));
        Finish();
    }

    /// <summary>The button beside a stopped batch's description, or none.</summary>
    private void SetFix(string? problem)
    {
        FixText = problem is null ? "" : SitesBrowseRules.FixButtonText(problem);
        FixVisibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void UpdateFile(SiteTransferDto t, SiteTransferItemDto item)
    {
        Kind = TransferRowKind.File;
        Ref = new TransferRef(t, item);
        BatchId = t.Id;
        IsExpanded = false;
        var status = Status(t, item);
        Name = item.Path;
        Indent = new Thickness(FileIndent, 0, 0, 0);
        ChevronVisibility = Visibility.Collapsed;
        SetDirection(IsDownload(t), status.Percent);
        SizeText = SiteTransferRules.SizeText(item);
        StatusText = status.Text;
        StatusBrush = BrushOf(status.Kind);
        Percent = status.Percent ?? 0;
        EtaText = TimeLeft(t, item);
        RateText = SiteTransferRules.RateText(t, item);
        Description = SiteTransferRules.Description(item);
        ToolTip = Description.Length > 0 ? $"{item.Path}\n{Description}" : item.Path;
        SetFix(null);
        Finish();
    }

    /// <summary>The "Show more" line of a batch with more files than are shown, or a plain note ("Reading the files").</summary>
    internal void UpdateLine(SiteTransferDto t, TransferRowKind kind, string text)
    {
        Kind = kind;
        Ref = null;
        BatchId = t.Id;
        IsExpanded = false;
        Name = text;
        Indent = new Thickness(FileIndent, 0, 0, 0);
        ChevronVisibility = Visibility.Collapsed;
        DownArrowVisibility = UpArrowVisibility = Visibility.Collapsed;
        SizeText = StatusText = EtaText = RateText = Description = "";
        StatusBrush = StatusBrushes.Neutral;
        Percent = 0;
        DownBarVisibility = UpBarVisibility = Visibility.Collapsed;
        ToolTip = kind == TransferRowKind.More ? "Show the next files of this transfer" : null;
        SetFix(null);
        Finish();
    }

    private void SetDirection(bool download, double? percent)
    {
        DownArrowVisibility = download ? Visibility.Visible : Visibility.Collapsed;
        UpArrowVisibility = download ? Visibility.Collapsed : Visibility.Visible;
        DownBarVisibility = download && percent is not null ? Visibility.Visible : Visibility.Collapsed;
        UpBarVisibility = !download && percent is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Finish()
    {
        AccessibleName = Kind is TransferRowKind.More or TransferRowKind.Note ? Name
            : $"{Name}. {StatusText}. {EtaText}. {Description}{(FixText.Length > 0 ? " " + FixText : "")}";
        // Only a row whose figures changed is told so: a list of hundreds of files is touched once a second.
        var stamp = string.Join('\u001f', Name, ChevronGlyph, SizeText, StatusText, Percent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            EtaText, RateText, Description, ChevronVisibility, DownArrowVisibility, UpArrowVisibility, FixText, FixVisibility);
        if (stamp == _stamp) return;
        _stamp = stamp;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// The Transfers bar of the Remote files view (R3, SITES.11). It shows the batches the Host pushes, opens a batch to its
/// files, and asks the window to act on them; it never talks to the Host itself.
/// </summary>
public sealed partial class SiteTransfersBar : UserControl
{
    private const double MinBodyHeight = 140;

    /// <summary>One opened batch's files: read once, then again when the batch's counts change (and at most every two seconds).</summary>
    private sealed class FileSet
    {
        public List<SiteTransferItemDto>? Ordered;
        public string Signature = "";
        public DateTimeOffset FetchedAt;
        public bool Fetching;
        public string? Error;
        public int Shown = FilesPerPage;
    }

    private readonly ObservableCollection<SiteTransferRow> _rows = [];
    private readonly Dictionary<string, SiteTransferRow> _byKey = [];
    private readonly HashSet<string> _expanded = [];
    private readonly Dictionary<string, FileSet> _files = [];
    private readonly ToggleButton _activeTab;
    private readonly ToggleButton _failedTab;
    private readonly ToggleButton _doneTab;
    private List<SiteTransferDto> _transfers = [];
    private string _tab = ActiveTab;
    private bool _open;
    private bool _recheck;
    private bool _stale;
    private bool _dragging;
    private double _dragStartY;
    private double _dragStartHeight;
    private double? _wantedHeight;
    private XamlRoot? _root;

    public SiteTransfersBar()
    {
        InitializeComponent();
        RowsList.ItemsSource = _rows;
        RowsList.ContextRequested += RowsList_ContextRequested;
        _activeTab = Tab("Running and waiting", ActiveTab);
        _failedTab = Tab("Failed", FailedTab);
        _doneTab = Tab("Done", DoneTab);
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        choices.Children.Add(_activeTab);
        choices.Children.Add(_failedTab);
        choices.Children.Add(_doneTab);
        TabHost.Child = choices;
        AutomationProperties.SetName(TabHost, "Transfers shown");
        InboundUi.Follow(TabHost, "InboundDividerBrush", brush => TabHost.BorderBrush = brush);
        InboundUi.Follow(TabHost, "InboundChromeBrush", brush => TabHost.Background = brush);
        _activeTab.IsChecked = true;

        HeightHandle.PointerPressed += HeightHandle_PointerPressed;
        HeightHandle.PointerMoved += HeightHandle_PointerMoved;
        HeightHandle.PointerReleased += HeightHandle_PointerReleased;
        HeightHandle.PointerCaptureLost += (_, _) => EndHeightDrag();
        // The height and the "only while shown" rule need the window: both follow XamlRoot.Changed (size, hidden, shown).
        Loaded += (_, _) => WatchRoot();
        Unloaded += (_, _) => UnwatchRoot();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => CatchUp());
        Show(_transfers);
    }

    private void WatchRoot()
    {
        if (XamlRoot is not { } root || ReferenceEquals(root, _root)) return;
        UnwatchRoot();
        _root = root;
        _root.Changed += Root_Changed;
        ApplyHeight();
        CatchUp();
    }

    private void UnwatchRoot()
    {
        if (_root is null) return;
        _root.Changed -= Root_Changed;
        _root = null;
    }

    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // A smaller window clamps the list again; a window shown again reads what changed while it was hidden.
        if (!_dragging) ApplyHeight();
        CatchUp();
    }

    /// <summary>
    /// The list is built, and an opened batch's files read again, only while it is open and on screen: hidden, the pushed
    /// batches only update the slim bar (idle use first). It is built once when it shows again.
    /// </summary>
    private bool Live => _open && Visibility == Visibility.Visible && XamlRoot is { IsHostVisible: true };

    private void CatchUp()
    {
        if (!_stale || !Live) return;
        _stale = false;
        Rebuild();
    }

    private void RebuildIfLive()
    {
        if (Live)
        {
            _stale = false;
            Rebuild();
        }
        else
        {
            _stale = true;
        }
    }

    private ToggleButton Tab(string text, string tab)
    {
        var button = MainWindow.ViewChoice(text);
        button.Click += (_, _) => SelectTab(tab);
        return button;
    }

    /// <summary>Pause, resume, cancel, retry or clear (a message type) on a batch id; the answer says whether it worked.</summary>
    internal Func<string, string, Task<SiteTransferResultDto>>? Act { get; set; }

    /// <summary>Stop one file or start it again (a message type), by batch id and the file's path.</summary>
    internal Func<string, string, string, Task<SiteTransferResultDto>>? ActFile { get; set; }

    /// <summary>One batch with its files.</summary>
    internal Func<string, Task<SiteTransferResultDto>>? Details { get; set; }

    /// <summary>
    /// SITES.10 (6): a stopped batch's button (Sign in, Check the certificate) for its problem; the window asks what
    /// browsing asks (the sign-in, the certificate, the key) and then tries the batch again. Without it no button shows.
    /// </summary>
    internal Func<SiteTransferDto, Task>? Fix { get; set; }

    /// <summary>Open, Open folder and Show on the remote are about this PC and the Remote files view: the window does them.</summary>
    internal event Action<TransferCommand, TransferRef>? LocalCommandRequested;

    /// <summary>The opened list's height was changed by dragging (it is remembered by the window).</summary>
    internal event Action? HeightChanged;

    internal bool IsOpen => _open;

    /// <summary>
    /// The height the user chose for the opened list (remembered by the window), null for the default. It is applied, clamped
    /// to the window, once the bar has a window and whenever the window's size changes; only a drag changes it.
    /// </summary>
    internal double? WantedHeight
    {
        get => _wantedHeight;
        set
        {
            _wantedHeight = value is { } height && !double.IsNaN(height) ? height : null;
            ApplyHeight();
        }
    }

    private void ApplyHeight()
    {
        if (XamlRoot is null) return;
        BodyPanel.Height = Clamped(_wantedHeight ?? 260);
    }

    private double Clamped(double height) => Math.Clamp(height, MinBodyHeight, MaxBodyHeight());

    private double MaxBodyHeight() => Math.Max(MinBodyHeight, (XamlRoot?.Size.Height ?? 900) - 260);

    /// <summary>Opens or closes the list above the bar.</summary>
    internal void SetOpen(bool open)
    {
        _open = open;
        BodyPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        // Up when closed (it opens upward), down when open.
        Chevron.Glyph = open ? "" : "";
        AutomationProperties.SetName(ToggleButton, open ? "Hide transfers" : "Show transfers");
        HeightHandle.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) RebuildIfLive();
    }

    /// <summary>Opens on Running and waiting: a transfer was just started.</summary>
    internal void OpenOnRunning()
    {
        SelectTab(ActiveTab);
        SetOpen(true);
    }

    /// <summary>The files of a batch as last read for the list, or null when they were not read.</summary>
    internal IReadOnlyList<SiteTransferItemDto>? FilesOf(string id) => _files.TryGetValue(id, out var set) ? set.Ordered : null;

    /// <summary>From the status chip: on the tab worth looking at, failures first, then what runs.</summary>
    internal void OpenAtBest()
    {
        var tab = _transfers.Any(t => t.State == "failed") ? FailedTab : ActiveTab;
        SelectTab(tab);
        SetOpen(true);
    }

    /// <summary>Every batch the Host has now.</summary>
    internal void Show(IReadOnlyList<SiteTransferDto> transfers)
    {
        _transfers = [.. transfers];
        // A batch that is gone (cleared) leaves its files and its open state behind.
        foreach (var id in _expanded.Where(id => _transfers.All(t => t.Id != id)).ToList()) _expanded.Remove(id);
        foreach (var id in _files.Keys.Where(id => _transfers.All(t => t.Id != id)).ToList()) _files.Remove(id);

        DownSpeedText.Text = SpeedText(DownSpeed(_transfers));
        UpSpeedText.Text = SpeedText(UpSpeed(_transfers));
        SummaryText.Text = SlimSummary(_transfers);
        SetLabel(_activeTab, ActiveTab);
        SetLabel(_failedTab, FailedTab);
        SetLabel(_doneTab, DoneTab);
        ClearDoneButton.IsEnabled = ClearDoneTargets(_transfers).Count > 0;
        ClearDoneButton.Visibility = _tab == FailedTab ? Visibility.Collapsed : Visibility.Visible;
        var retryAll = _tab == FailedTab && RetryAll(_transfers).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (RetryAllButton.Visibility != retryAll) RetryAllButton.Visibility = retryAll;
        // Closed or hidden, nothing is built and no files are read: the list catches up when it shows.
        RebuildIfLive();
    }

    private void SetLabel(ToggleButton button, string tab)
    {
        var label = TabLabel(_transfers, tab);
        if (button.Content as string == label) return;
        button.Content = label;
        AutomationProperties.SetName(button, label);
    }

    private SiteTransferRow Row(string key)
    {
        if (_byKey.TryGetValue(key, out var existing)) return existing;
        return new SiteTransferRow(key);
    }

    /// <summary>The list as it should be now: each batch of the tab, and under an opened one its files (the first page).</summary>
    private void Rebuild()
    {
        var desired = new List<SiteTransferRow>();
        // Keys are unique: two files listed with one path show once, and the "\0" lines can't equal a path.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in InTab(_transfers, _tab))
        {
            var row = Row(t.Id);
            var expanded = _expanded.Contains(t.Id) && (t.Files > 1 || t.Folders > 0);
            row.UpdateBatch(t, expanded);
            desired.Add(row);
            if (!expanded) continue;
            EnsureFiles(t);
            if (_files.TryGetValue(t.Id, out var set) && set.Ordered is { } ordered)
            {
                var shown = Math.Min(set.Shown, ordered.Count);
                for (var index = 0; index < shown; index++)
                {
                    var item = ordered[index];
                    var key = t.Id + "\n" + item.Path;
                    if (!keys.Add(key)) continue;
                    var fileRow = Row(key);
                    fileRow.UpdateFile(t, item);
                    desired.Add(fileRow);
                }

                if (shown < ordered.Count)
                {
                    var more = Row(t.Id + "\0more");
                    more.UpdateLine(t, TransferRowKind.More, MoreText(ordered.Count - shown));
                    desired.Add(more);
                }
            }
            else
            {
                var note = Row(t.Id + "\0note");
                note.UpdateLine(t, TransferRowKind.Note, set?.Error ?? "Reading the files…");
                desired.Add(note);
            }
        }

        // A moved row loses its container and so its selection: the selection is kept by row key instead.
        var selected = RowsList.SelectedItems.OfType<SiteTransferRow>().Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        // So is the keyboard focus: a moved row's container is new.
        var focusedBefore = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as ListViewItem : null;
        var focusedKey = focusedBefore is { Content: SiteTransferRow focusedRow } ? focusedRow.Key : null;
        Reconcile(desired);
        if (focusedKey is not null && _byKey.TryGetValue(focusedKey, out var focusAgain)) KeepFocus(focusAgain, focusedBefore!);
        if (selected.Count > 0)
        {
            foreach (var row in _rows)
            {
                if (selected.Contains(row.Key) && !RowsList.SelectedItems.Contains(row)) RowsList.SelectedItems.Add(row);
            }
        }

        EmptyText.Text = _rows.Count > 0 ? "" : EmptyMessage(_tab);
    }

    /// <summary>Puts <see cref="_rows"/> in the order of <paramref name="desired"/> with as few moves as it can (the rule is tested).</summary>
    private void Reconcile(List<SiteTransferRow> desired)
    {
        SiteTransferRules.Reconcile(_rows, desired, (from, to) => _rows.Move(from, to));
        _byKey.Clear();
        foreach (var row in _rows) _byKey[row.Key] = row;
    }

    /// <summary>Puts the keyboard back on <paramref name="row"/> once the list has its containers, unless it went elsewhere meanwhile.</summary>
    private void KeepFocus(SiteTransferRow row, ListViewItem before)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (XamlRoot is not { } root || RowsList.ContainerFromItem(row) is not ListViewItem container) return;
            var now = FocusManager.GetFocusedElement(root) as DependencyObject;
            if (ReferenceEquals(now, container)) return;
            // The row got another container, or focus went to another row's: focus that fell to nothing or stayed in the list
            // comes back to the row; focus the user moved out of the list (a tab, the toolbar) stays.
            if (now is null || (!ReferenceEquals(container, before) || now is ListViewItem) && Inside(now, RowsList))
                container.Focus(FocusState.Programmatic);
        });
    }

    private static bool Inside(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor)) return true;
        }

        return false;
    }

    /// <summary>
    /// The files of an opened batch are read when it opens and again when its counts change, no sooner than two seconds after
    /// the last read. Nothing shows a thousand files at once: the list takes the first page and "Show more" the next.
    /// </summary>
    private void EnsureFiles(SiteTransferDto t)
    {
        if (!_files.TryGetValue(t.Id, out var set)) _files[t.Id] = set = new FileSet();
        if (set.Fetching || Details is null) return;
        var signature = FilesSignature(t);
        // Read, or failed to read, for these counts: nothing to do until they change.
        if ((set.Ordered is not null || set.Error is not null) && set.Signature == signature) return;
        // Every two seconds at most; a list of more than a thousand files every ten (each read is the whole list).
        var wait = FilesReadInterval(set.Ordered?.Count ?? 0) - (DateTimeOffset.UtcNow - set.FetchedAt);
        if (set.FetchedAt != default && wait > TimeSpan.Zero)
        {
            // Changed again soon after a read: look again when the two seconds are over, even if nothing else pushes an update.
            if (!_recheck)
            {
                _recheck = true;
                _ = Task.Delay(wait + TimeSpan.FromMilliseconds(100)).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                {
                    _recheck = false;
                    RebuildIfLive();
                }), TaskScheduler.Default);
            }

            return;
        }

        _ = FetchFilesAsync(t.Id, set, signature);
    }

    private async Task FetchFilesAsync(string id, FileSet set, string signature)
    {
        set.Fetching = true;
        try
        {
            var reply = await Details!(id);
            if (!reply.Ok || reply.Items is not { } items)
            {
                // Said in the batch's note row (the list keeps it open); read again when the counts change or it is reopened.
                set.Error = "The files could not be read: " + (reply.Message ?? "no answer.");
                set.Signature = signature;
            }
            else
            {
                set.Error = null;
                set.Ordered = OrderItems(items);
                set.Signature = reply.Transfer is { } fresh ? FilesSignature(fresh) : signature;
            }
        }
        catch (Exception ex)
        {
            set.Error = "The files could not be read: " + ex.Message;
            set.Signature = signature;
        }
        finally
        {
            set.FetchedAt = DateTimeOffset.UtcNow;
            set.Fetching = false;
            // Every path ends with the list built again, so "Reading the files…" never stays.
            RebuildIfLive();
        }
    }

    private void SelectTab(string tab)
    {
        _tab = tab;
        _activeTab.IsChecked = tab == ActiveTab;
        _failedTab.IsChecked = tab == FailedTab;
        _doneTab.IsChecked = tab == DoneTab;
        ErrorText.Text = "";
        Show(_transfers);
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) => SetOpen(!_open);

    // ---- Opening a batch --------------------------------------------------------------------------------

    private void Toggle(SiteTransferRow row, bool? open = null)
    {
        if (row.Kind != TransferRowKind.Batch || row.ChevronVisibility != Visibility.Visible) return;
        var wanted = open ?? !_expanded.Contains(row.BatchId);
        if (wanted)
        {
            _expanded.Add(row.BatchId);
            // A read that failed is tried again when the batch is opened again.
            if (_files.TryGetValue(row.BatchId, out var failed) && failed.Error is not null) _files.Remove(row.BatchId);
        }
        else
        {
            // Closed, its files are not kept (a batch of thousands): opening reads them again.
            _expanded.Remove(row.BatchId);
            _files.Remove(row.BatchId);
        }

        Rebuild();
    }

    private void ShowMore(SiteTransferRow row)
    {
        if (_files.TryGetValue(row.BatchId, out var set)) set.Shown += FilesPerPage;
        Rebuild();
    }

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SiteTransferRow row }) return;
        // A double click on the chevron is two Clicks: the second, on the same row within the double-click time, is ignored,
        // so the batch stays open rather than opening and closing.
        var now = Environment.TickCount64;
        if (ReferenceEquals(row, _chevronRow) && now - _chevronAt <= DoubleClickTime()) return;
        _chevronRow = row;
        _chevronAt = now;
        Toggle(row);
    }

    private SiteTransferRow? _chevronRow;
    private long _chevronAt;

    private static long DoubleClickTime() => OperatingSystem.IsWindows() ? NativeClick.GetDoubleClickTime() : 500;

    private static class NativeClick
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetDoubleClickTime();
    }

    private void Chevron_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

    // ---- Selection and commands -------------------------------------------------------------------------

    private List<TransferRef> Selected() =>
        [.. RowsList.SelectedItems.OfType<SiteTransferRow>().Where(row => row.Ref is not null).Select(row => row.Ref!.Value)];

    private void RowsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SiteTransferRow row) Activate(row);
    }

    /// <summary>Double click and Enter: Open on a finished row, Show more on that line, and open or close any other batch.</summary>
    private void Activate(SiteTransferRow row)
    {
        if (row.Kind == TransferRowKind.More)
        {
            ShowMore(row);
            return;
        }

        if (row.Ref is not { } reference) return;
        if (Primary(reference) is { } command) Do(command, [reference]);
        else Toggle(row);
    }

    private void RowsList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var rows = RowsList.SelectedItems.OfType<SiteTransferRow>().ToList();
        var selection = Selected();
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter when control && selection.Count == 1:
                Do(TransferCommand.OpenFolder, selection);
                break;
            case Windows.System.VirtualKey.Enter when rows.Count == 1:
                Activate(rows[0]);
                break;
            case Windows.System.VirtualKey.Delete:
                if (!Do(TransferCommand.Remove, selection)) return;
                break;
            case Windows.System.VirtualKey.F5:
                if (!Do(TransferCommand.Retry, selection)) return;
                break;
            case Windows.System.VirtualKey.Space when !control && SpaceCommand(selection) is { } space:
                Do(space, selection);
                break;
            case Windows.System.VirtualKey.Right when rows.Count == 1 && rows[0].Kind == TransferRowKind.Batch:
                Toggle(rows[0], true);
                break;
            case Windows.System.VirtualKey.Left when rows.Count == 1 && rows[0].Kind == TransferRowKind.Batch:
                Toggle(rows[0], false);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Right click (the row under the pointer is selected first unless it already is), Shift+F10 or the menu key.</summary>
    private void RowsList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var byPointer = e.TryGetPosition(RowsList, out var point);
        if (byPointer)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is SiteTransferRow under && under.Ref is not null)
            {
                if (!RowsList.SelectedItems.Contains(under))
                {
                    RowsList.SelectedItems.Clear();
                    RowsList.SelectedItems.Add(under);
                }
            }
            else if (RowsList.SelectedItems.Count > 0)
            {
                RowsList.SelectedItems.Clear();
            }
        }

        var selection = Selected();
        var groups = Menu(selection);
        e.Handled = true;
        if (groups.Count == 0) return;
        var menu = new MenuFlyout();
        foreach (var group in groups)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var command in group)
            {
                var item = new MenuFlyoutItem { Text = Label(command) };
                if (KeyText(command) is { Length: > 0 } key) item.KeyboardAcceleratorTextOverride = CommandKey.Text(key);
                var chosen = command;
                item.Click += (_, _) => Do(chosen, selection);
                menu.Items.Add(item);
            }
        }

        if (byPointer)
        {
            menu.ShowAt(RowsList, new FlyoutShowOptions { Position = point });
            return;
        }

        var selected = RowsList.SelectedIndex >= 0 ? RowsList.ContainerFromIndex(RowsList.SelectedIndex) as FrameworkElement : null;
        menu.ShowAt(selected ?? RowsList);
    }

    /// <summary>Runs <paramref name="command"/> on the rows it is valid for; a problem is shown above the list. False when it
    /// applied to none of them (a key then goes on to the list).</summary>
    private bool Do(TransferCommand command, IReadOnlyList<TransferRef> rows)
    {
        var valid = rows.Where(row => Valid(command, row)).ToList();
        if (valid.Count == 0) return false;
        if (OneRowOnly(command))
        {
            if (valid.Count != 1 || rows.Count != 1) return false;
            LocalCommandRequested?.Invoke(command, valid[0]);
            return true;
        }

        _ = RunAsync(command, valid);
        return true;
    }

    private async Task RunAsync(TransferCommand command, List<TransferRef> rows)
    {
        if (Act is not { } act) return;
        ErrorText.Text = "";
        var problems = new List<string>();
        try
        {
            await RunEachAsync(command, rows, act, problems);
        }
        catch (Exception ex)
        {
            problems.Add("That didn't work: " + ex.Message);
        }

        if (problems.Count > 0) ErrorText.Text = string.Join(" ", problems);
    }

    private async Task RunEachAsync(TransferCommand command, List<TransferRef> rows, Func<string, string, Task<SiteTransferResultDto>> act, List<string> problems)
    {
        foreach (var row in rows)
        {
            SiteTransferResultDto reply;
            if (row.Item is { } item)
            {
                if (ActFile is not { } actFile) continue;
                reply = await actFile(command == TransferCommand.StopFile ? SitesMessageTypes.TransferStopFile : SitesMessageTypes.TransferStartFile,
                    row.Batch.Id, item.Path);
            }
            else
            {
                var type = command switch
                {
                    TransferCommand.Stop => SitesMessageTypes.TransferPause,
                    TransferCommand.Resume => SitesMessageTypes.TransferResume,
                    TransferCommand.Retry => SitesMessageTypes.TransferRetry,
                    TransferCommand.Cancel => SitesMessageTypes.TransferCancel,
                    _ => SitesMessageTypes.TransferClear,
                };
                reply = await act(type, row.Batch.Id);
            }

            if (!reply.Ok && (reply.Message ?? "That didn't work.") is var message && !problems.Contains(message)) problems.Add(message);
        }
    }

    /// <summary>
    /// SITES.10 (6): the button on a stopped batch. The window asks what browsing asks about the problem (the sign-in,
    /// the certificate, the key), then tries the batch again; a fix that did not work brings the same button back.
    /// </summary>
    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SiteTransferRow { Ref: { } reference } } || Fix is not { } fix) return;
        ErrorText.Text = "";
        try
        {
            await fix(reference.Batch);
        }
        catch (Exception ex)
        {
            ErrorText.Text = "That didn't work: " + ex.Message;
        }
    }

    private async void ClearDoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (Act is not { } act) return;
        ErrorText.Text = "";
        // One id at a time: an empty id would clear the failed batches too.
        var problems = new List<string>();
        try
        {
            foreach (var id in ClearDoneTargets(_transfers))
            {
                var reply = await act(SitesMessageTypes.TransferClear, id);
                if (!reply.Ok && (reply.Message ?? "That didn't work.") is var message && !problems.Contains(message)) problems.Add(message);
            }
        }
        catch (Exception ex)
        {
            problems.Add("That didn't work: " + ex.Message);
        }

        if (problems.Count > 0) ErrorText.Text = string.Join(" ", problems);
    }

    private async void RetryAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Act is not { } act) return;
        ErrorText.Text = "";
        RetryAllButton.IsEnabled = false;
        try
        {
            var problems = new List<string>();
            foreach (var id in RetryAll(_transfers))
            {
                var reply = await act(SitesMessageTypes.TransferRetry, id);
                if (!reply.Ok && reply.Message is { Length: > 0 } message && !problems.Contains(message)) problems.Add(message);
            }

            if (problems.Count > 0) ErrorText.Text = string.Join(" ", problems);
        }
        catch (Exception ex)
        {
            ErrorText.Text = "That didn't work: " + ex.Message;
        }
        finally
        {
            RetryAllButton.IsEnabled = true;
        }
    }

    // ---- Height -----------------------------------------------------------------------------------------

    private void HeightHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed || !HeightHandle.CapturePointer(e.Pointer)) return;
        _dragging = true;
        _dragStartY = e.GetCurrentPoint(null).Position.Y;
        _dragStartHeight = BodyPanel.Height;
        e.Handled = true;
    }

    private void HeightHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        // The whole bar grows upward from the window's bottom edge, so the window's coordinates are the steady ones.
        _wantedHeight = Clamped(_dragStartHeight + (_dragStartY - e.GetCurrentPoint(null).Position.Y));
        BodyPanel.Height = _wantedHeight.Value;
        e.Handled = true;
    }

    private void HeightHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        HeightHandle.ReleasePointerCapture(e.Pointer);
        EndHeightDrag();
        e.Handled = true;
    }

    private void EndHeightDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        HeightChanged?.Invoke();
    }
}
