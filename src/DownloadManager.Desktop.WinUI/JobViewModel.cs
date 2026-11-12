using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

public sealed class JobViewModel : INotifyPropertyChanged
{
    private string _fileName = string.Empty;
    private string _url = string.Empty;
    private string _state = string.Empty;
    private string _stateText = string.Empty;
    private string _sizeText = string.Empty;
    private string _speedText = string.Empty;
    private string _etaText = string.Empty;
    private string _detailText = string.Empty;
    private double _progressPercent;
    private bool _canPause;
    private bool _canResume;
    private bool _isTerminal;
    private string _sourceText = string.Empty;
    private string _destination = string.Empty;
    private string _finalFilePath = string.Empty;
    private string _fileGlyph = "\uE7C3";
    private ImageSource? _fileIcon;
    private string? _iconKey;

    /// <summary>
    /// T11: the download is on another server, for a row a progress window shows (that window's own Host). Null: the
    /// server the main window shows (<see cref="ShownServer"/>).
    /// </summary>
    internal bool? OnAnotherServer { get; set; }
    public ImageSource? FileIcon { get => _fileIcon; private set => Set(ref _fileIcon, value); }
    // A torrent's own glyph takes the type icon's place, in the swarm colour (TorrentGlyphVisibility).
    public Visibility FileIconVisibility => FileIcon is null || IsTorrent ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FileGlyphVisibility => FileIcon is null && !IsTorrent ? Visibility.Visible : Visibility.Collapsed;
    private string _queueText = string.Empty;
    private string _lastTryText = string.Empty;
    private string _description = string.Empty;
    private string _rawDescription = string.Empty;
    private Visibility _progressVisibility = Visibility.Collapsed;
    private DateTimeOffset _createdAt;

    public Guid Id { get; private set; }
    public int QueueId { get; private set; }
    public int Priority { get; private set; }
    public string CategoryId { get; private set; } = "other";
    public bool CategoryIsAutomatic { get; private set; } = true;
    public JobCompletionOptionsDto CompletionOptions { get; private set; } = new();
    public string QueueName { get => _queueName; private set => Set(ref _queueName, value); }
    private string _queueName = "Main";
    /// <summary>For a package row: the queue all its files are in, if they share one.</summary>
    public int? SharedQueueId { get; private set; }
    /// <summary>For a package row: whether its Cancel would stop any file.</summary>
    public bool CanCancelPackage { get; private set; }

    public string FileName { get => _fileName; private set => Set(ref _fileName, value); }
    public string Url { get => _url; private set => Set(ref _url, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string SizeText
    {
        get => _sizeText;
        private set
        {
            if (Set(ref _sizeText, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeVisibility)));
        }
    }
    /// <summary>Collapses an empty size, so text beside it in a row starts where the size would (Scheduler's Files tab).</summary>
    public Visibility SizeVisibility => _sizeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
    public bool CanPause { get => _canPause; private set => Set(ref _canPause, value); }
    public bool CanResume { get => _canResume; private set => Set(ref _canResume, value); }
    public bool IsTerminal { get => _isTerminal; private set => Set(ref _isTerminal, value); }
    public string SourceText { get => _sourceText; private set => Set(ref _sourceText, value); }
    public string Destination { get => _destination; private set => Set(ref _destination, value); }
    public string FinalFilePath { get => _finalFilePath; private set => Set(ref _finalFilePath, value); }
    public string FileGlyph { get => _fileGlyph; private set => Set(ref _fileGlyph, value); }
    public string QueueText { get => _queueText; private set => Set(ref _queueText, value); }
    public string LastTryText { get => _lastTryText; private set => Set(ref _lastTryText, value); }
    public string Description { get => _description; private set => Set(ref _description, value); }
    public string RawDescription { get => _rawDescription; private set => Set(ref _rawDescription, value); }
    public long TotalSize { get; private set; }
    /// <summary>Null when there is no estimate, and that row has nothing to sort by.</summary>
    public TimeSpan? Remaining { get; private set; }
    public double BytesPerSecond { get; private set; }
    public DateTimeOffset LastUpdated { get; private set; }
    public Visibility ProgressVisibility { get => _progressVisibility; private set => Set(ref _progressVisibility, value); }
    public Microsoft.UI.Xaml.Media.Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    private Microsoft.UI.Xaml.Media.Brush _statusBrush = NeutralStatus;

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush ActiveStatus = StatusBrushes.Active;
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush DoneStatus = StatusBrushes.Done;
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush FailedStatus = StatusBrushes.Failed;
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WaitingStatus = StatusBrushes.Waiting;
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush NeutralStatus = StatusBrushes.Neutral;

    /// <summary>Clear, so the whole row still takes clicks; a brief accent while the row is pointed out (EXT.R12).</summary>
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush NoHighlight = new(Microsoft.UI.Colors.Transparent);
    public Microsoft.UI.Xaml.Media.Brush RowHighlight { get => _rowHighlight; private set => Set(ref _rowHighlight, value); }
    private Microsoft.UI.Xaml.Media.Brush _rowHighlight = NoHighlight;

    /// <summary>EXT.R12: shows the row's brief highlight, or ends it with null.</summary>
    internal void Highlight(Microsoft.UI.Xaml.Media.Brush? brush) => RowHighlight = brush ?? NoHighlight;
    public DateTimeOffset CreatedAt { get => _createdAt; private set => Set(ref _createdAt, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Review 5:E1: the Status of a row that moves while the Host can't be reached.</summary>
    public const string NotCurrentStatus = "Status unknown";

    private bool Moves => IsPackage ? Children.Any(child => child.Moves) : SpeedSample.Moves(State, Dto?.Torrent?.Seeding == true);

    /// <summary>
    /// Review 5:E1: the Host was lost, so a row that moves no longer shows a speed, a time left or a state that may have
    /// ended. The next snapshot builds the rows afresh.
    /// </summary>
    public void ShowNotCurrent()
    {
        if (!Moves) return;
        StateText = NotCurrentStatus;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        BytesPerSecond = 0;
        StatusBrush = NeutralStatus;
    }

    public void Update(JobDto job)
    {
        var layoutBefore = TorrentLayoutKey;
        // Kept while it waits: the window may only get to ask once a dialog that was open has closed.
        FileChoiceArrived = TorrentRules.FileChoiceArrived(Dto, job) || (FileChoiceArrived && TorrentRules.PrimaryIsChooseFiles(job));
        Dto = job;
        IsPackage = false;
        Id = job.Id;
        FileName = job.FileName;
        // A magnet's trackers are never shown: a torrent row holds the short magnet (TorrentRules.ShortMagnet).
        Url = job.Torrent is not null ? JobAddress.Shown(job) : job.Url;
        State = job.State;
        StateText = TorrentRules.StatusText(job) is { } torrentStatus ? torrentStatus
            : job.QueueRetryAt is not null ? "Queue retry scheduled"
            : JobNotes.WaitingText(job) is { } waiting ? waiting
            : IsProviderFile && job.State == "downloading" ? "Downloading locally"
            : JobNotes.NotStartedText(job) ?? ToDisplayState(job.State);
        TotalSize = job.TotalBytes is > 0 ? job.TotalBytes.Value : job.ReceivedBytes;
        BytesPerSecond = job.BytesPerSecond ?? 0;
        LastUpdated = job.UpdatedAt;
        SizeText = TotalSize > 0 ? UiFormat.Bytes(TotalSize) : string.Empty;
        // T2: a seeding torrent's rate is what it sends.
        SpeedText = TorrentSeedRules.RateText(job) ?? (BytesPerSecond > 0 ? $"{UiFormat.Bytes((long)BytesPerSecond)}/s" : string.Empty);
        Remaining = job.EstimatedRemaining;
        EtaText = DurationText.TimeLeft(job.EstimatedRemaining);
        DetailText = JobAddress.Detail(job);
        ProgressPercent = job.Progress is not null
            ? Math.Clamp(job.Progress.Value * 100, 0, 100)
            : job.TotalBytes is > 0
                ? Math.Clamp(job.ReceivedBytes * 100.0 / job.TotalBytes.Value, 0, 100)
                : 0;
        CanPause = job.CanPause;
        CanResume = job.CanResume;
        IsTerminal = job.IsTerminal;
        SourceText = job.AddedFromAnotherDevice ? "Another device" : ToDisplaySource(job.Source);
        Destination = job.DestinationDirectory;
        FinalFilePath = job.FinalFilePath;
        // A torrent shows the swarm, or a link while a magnet still waits for its file list.
        FileGlyph = job.Torrent is { } shownTorrent ? shownTorrent.HasMetadata ? SwarmGlyph : MagnetGlyph : GlyphFor(FileName);
        StatusNote = JobNotes.Note(job);
        RowToolTip = StatusNote;

        // With no file there, the type's icon is all there is to show. A torrent shows its own glyph and loads none.
        var completedPath = job.State == "completed" && !job.FileRemovedAfterExtraction ? job.FinalFilePath : null;
        var iconKey = job.Torrent is not null ? TorrentIconKey
            : completedPath is null ? "type:" + Path.GetExtension(job.FileName)
            : $"file:{job.Id}:{completedPath}:{job.CompletedAt:O}";
        if (iconKey == TorrentIconKey)
        {
            if (_iconKey != iconKey)
            {
                _iconKey = iconKey;
                SetFileIcon(null);
            }
        }
        else if (_iconKey != iconKey)
        {
            _iconKey = iconKey;
            SetFileIcon(null);
            _ = LoadFileIconAsync(job.FileName, completedPath, iconKey);
        }
        QueueId = job.QueueId;
        Priority = job.Priority;
        CategoryId = job.CategoryId ?? FileCategories.For(job.FileName);
        CategoryIsAutomatic = job.CategoryIsAutomatic;
        CompletionOptions = job.CompletionOptions;
        QueueName = job.QueueName ?? (job.QueueId == 0 ? "Main" : $"Queue {job.QueueId}");
        QueueText = job.IsQueued ? (job.QueueId + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
        LastTryText = UiFormat.Moment(job.CompletedAt ?? job.UpdatedAt);
        RawDescription = job.Description ?? string.Empty;
        // A torrent says how many of its files it downloads, unless the user gave it words of their own.
        Description = JobNotes.Description(StatusNote,
            job.Torrent is { } described && string.IsNullOrWhiteSpace(job.Description) ? TorrentRules.FileListWaitNote(job, DateTimeOffset.UtcNow) ?? TorrentRules.FilesSummary(described) : JobAddress.Own(job));
        ProgressVisibility = (job.State is "downloading" or "connecting" or "retryWaiting" or "verifying" or "paused") &&
                             ProgressPercent > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CreatedAt = job.CreatedAt;
        UpdateTorrentColumns(job);
        StatusBrush = job.State switch
        {
            "downloading" or "connecting" or "verifying" or "assembling" => ActiveStatus,
            "completed" => DoneStatus,
            "failed" => FailedStatus,
            "paused" or "queued" or "retryWaiting" or "waiting" => WaitingStatus,
            _ => NeutralStatus,
        };
        if (TorrentLayoutKey != layoutBefore) NotifyPackageLayout();
    }

    // ---- Packages (JDownloader-style grouping of several files in the list) ----

    private Guid? _packageId;
    private bool _isPackage;
    private bool _isExpanded;

    /// <summary>The last DTO applied to this row; null for package rows.</summary>
    public JobDto? Dto { get; private set; }

    /// <summary>True for the expandable package header row.</summary>
    public bool IsPackage { get => _isPackage; private set { if (Set(ref _isPackage, value)) NotifyPackageLayout(); } }

    /// <summary>For a file row: the package it is listed under while that package is shown.</summary>
    public Guid? PackageId { get => _packageId; set { if (Set(ref _packageId, value)) NotifyPackageLayout(); } }

    public bool IsExpanded { get => _isExpanded; private set { if (Set(ref _isExpanded, value)) NotifyPackageLayout(); } }

    /// <summary>The file rows of a package row, in display order.</summary>
    public IReadOnlyList<JobViewModel> Children { get; private set; } = [];

    /// <summary>A package's file sits in a little, inside its first two cells, because the columns after them must stay under their headers.</summary>
    private bool IsIndented => (!IsPackage && PackageId is not null) || IsTorrentFile;
    public Thickness IconIndent => IsIndented ? new Thickness(RowIndent, 0, 0, 0) : default;

    /// <summary>An open torrent row's name starts after its swarm glyph, which sits where a package row has none.</summary>
    public Thickness NameIndent => IsIndented ? new Thickness(RowIndent, 0, 0, 0)
        : IsTorrent && HasChevron ? new Thickness(TorrentGlyphRoom, 0, 0, 0) : default;
    private const double RowIndent = 24;
    private const double TorrentGlyphRoom = 22;
    public string ChevronGlyph => IsExpanded ? "" : "";

    /// <summary>A package row, or a torrent row with several known files (it lists them, not jobs: see <see cref="IsTorrentFile"/>).</summary>
    public bool HasChevron => IsPackage || ShowsTorrentFiles;
    public Visibility ChevronVisibility => HasChevron ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FileIconAreaVisibility => HasChevron ? Visibility.Collapsed : Visibility.Visible;
    public Windows.UI.Text.FontWeight NameWeight => IsPackage || IsTorrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>The chevron's spoken name and tooltip.</summary>
    public string ChevronName => IsTorrent ? "Show or hide files" : "Expand or collapse package";

    private void NotifyPackageLayout()
    {
        foreach (var name in new[] { nameof(IconIndent), nameof(NameIndent), nameof(ChevronGlyph), nameof(ChevronVisibility), nameof(FileIconAreaVisibility), nameof(NameWeight),
                     nameof(ChevronName), nameof(TorrentGlyphVisibility), nameof(TorrentNameGlyphVisibility), nameof(TorrentVisibility), nameof(FileIconVisibility), nameof(FileGlyphVisibility) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ---- Torrents (T1): a torrent is one job; its files are display rows under it, never jobs ----

    private const string SwarmGlyph = "";
    private const string MagnetGlyph = "";
    private const string TorrentIconKey = "torrent";

    /// <summary>A torrent job's own row. Never a package: its files are no jobs, so no command may act on them.</summary>
    public bool IsTorrent => !IsTorrentFile && Dto?.Torrent is not null;

    /// <summary>The torrent's files are known and there are several, so the row opens to list them.</summary>
    public bool ShowsTorrentFiles => IsTorrent && TorrentRules.ShowsFiles(Dto!.Torrent);

    /// <summary>
    /// This row saw its torrent start waiting for a file choice (the list came in), rather than being built already
    /// waiting from a snapshot; it stays so while the torrent waits. See <see cref="TorrentRules.FileChoiceArrived"/>.
    /// </summary>
    public bool FileChoiceArrived { get; private set; }

    /// <summary>A display row under an open torrent row: one of its files, or a line about them. A side row: no command acts on it.</summary>
    public bool IsTorrentFile { get; private set; }

    /// <summary>For a torrent's file row: the torrent job it belongs to.</summary>
    public Guid? TorrentParentId { get; private set; }

    /// <summary>For a torrent's file row: the line that sends the rest of a long list to Progress Details.</summary>
    public bool IsTorrentMore { get; private set; }

    /// <summary>The swarm glyph in the icon cell, for a torrent row with no chevron there.</summary>
    public Visibility TorrentGlyphVisibility => IsTorrent && !HasChevron ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The swarm glyph where a list has no chevron, such as the Scheduler's.</summary>
    public Visibility TorrentVisibility => IsTorrent ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The swarm glyph before the name, for a torrent row whose icon cell holds the chevron.</summary>
    public Visibility TorrentNameGlyphVisibility => IsTorrent && HasChevron ? Visibility.Visible : Visibility.Collapsed;

    private (bool Torrent, bool Files, bool Metadata) TorrentLayoutKey => (IsTorrent, ShowsTorrentFiles, Dto?.Torrent?.HasMetadata == true);

    // ---- The torrent views' columns and seeding goal (DT-6) ----

    private string _seedsText = string.Empty;
    private string _peersText = string.Empty;
    private string _upText = string.Empty;
    private string _ratioText = string.Empty;
    private double _seedGoalPercent;
    private Visibility _seedGoalVisibility = Visibility.Collapsed;
    private string? _seedGoalToolTip;

    /// <summary>The Host's seeding goal as the window last read its settings; the window sets it (MainWindow.Torrents).</summary>
    internal static SeedGoal SeedGoal { get; set; }

    /// <summary>A torrent view is shown, so the Seeds, Peers, Up and Ratio cells take room.</summary>
    internal static bool ShowTorrentColumns { get; set; }

    public Visibility TorrentColumnsVisibility => ShowTorrentColumns ? Visibility.Visible : Visibility.Collapsed;

    internal void NotifyTorrentColumns() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TorrentColumnsVisibility)));

    public string SeedsText { get => _seedsText; private set => Set(ref _seedsText, value); }
    public string PeersText { get => _peersText; private set => Set(ref _peersText, value); }
    public string UpText { get => _upText; private set => Set(ref _upText, value); }
    public string RatioText { get => _ratioText; private set => Set(ref _ratioText, value); }

    /// <summary>For sorting: null where the cell is blank.</summary>
    public int? SeedCount { get; private set; }
    public int? PeerCount { get; private set; }
    public double UploadBytesPerSecond { get; private set; }
    public double? RatioValue { get; private set; }

    /// <summary>A seeding torrent's way to its seeding goal, in a bar of its own under the status.</summary>
    public double SeedGoalPercent { get => _seedGoalPercent; private set => Set(ref _seedGoalPercent, value); }
    public Visibility SeedGoalVisibility { get => _seedGoalVisibility; private set => Set(ref _seedGoalVisibility, value); }
    public string? SeedGoalToolTip { get => _seedGoalToolTip; private set => Set(ref _seedGoalToolTip, value); }

    /// <summary>
    /// Seeds, Peers, Up and Ratio; a seeding torrent's bar and Time left are its way to the seeding goal. Read again on
    /// each update of the job (and when the goal changes): no timer.
    /// </summary>
    private void UpdateTorrentColumns(JobDto job)
    {
        SeedCount = TorrentListRules.Seeds(job);
        PeerCount = TorrentListRules.OtherPeers(job);
        UploadBytesPerSecond = TorrentListRules.UploadRate(job) ?? 0;
        RatioValue = job.Torrent?.Ratio;
        SeedsText = TorrentListRules.CountCell(SeedCount);
        PeersText = TorrentListRules.CountCell(PeerCount);
        UpText = TorrentListRules.UpCell(job);
        RatioText = TorrentListRules.RatioCell(job);

        var now = DateTimeOffset.UtcNow;
        var goal = TorrentListRules.GoalProgress(job, SeedGoal, now);
        SeedGoalPercent = goal is { } progress ? progress * 100 : 0;
        SeedGoalVisibility = goal is not null ? Visibility.Visible : Visibility.Collapsed;
        SeedGoalToolTip = goal is not null && TorrentListRules.GoalText(SeedGoal.For(job)) is { } text ? $"Seeding goal: {text}" : null;
        if (TorrentSeedRules.IsSeeding(job))
        {
            Remaining = TorrentListRules.TimeToGoal(job, SeedGoal, now);
            EtaText = DurationText.TimeLeft(Remaining);
        }
    }

    /// <summary>The seeding goal changed: a seeding row reads its way to it again.</summary>
    internal void RefreshSeedGoal()
    {
        if (!IsTorrentFile && Dto is { } job && TorrentSeedRules.IsSeeding(job)) UpdateTorrentColumns(job);
    }

    /// <summary>Opens or closes a torrent row; the window lists its file rows under it.</summary>
    internal void SetTorrentExpanded(bool expanded) => IsExpanded = expanded && ShowsTorrentFiles;

    /// <summary>Shows one of a torrent's files: name, Size and Status. One rate and one time left cover the whole torrent.</summary>
    internal void UpdateTorrentFile(Guid rowId, Guid parentId, TorrentFileDto file, string parentState, Microsoft.UI.Xaml.Media.Brush parentBrush)
    {
        var first = !IsTorrentFile;
        IsTorrentFile = true;
        IsTorrentMore = false;
        Id = rowId;
        TorrentParentId = parentId;
        FileName = TorrentRules.FileRowName(file.Path);
        State = "torrentFile";
        StateText = TorrentRules.FileRowStatus(file, ToDisplayState(parentState));
        StatusBrush = !file.Wanted ? NeutralStatus : StateText == "Done" ? DoneStatus : parentBrush;
        TotalSize = file.Size;
        SizeText = file.Size > 0 ? UiFormat.Bytes(file.Size) : string.Empty;
        // Its folders, which the name leaves out.
        RowToolTip = file.Path.Replace('/', Path.DirectorySeparatorChar);
        DetailText = file.Path;
        Description = string.Empty;
        FileGlyph = GlyphFor(FileName);
        var iconKey = "type:" + Path.GetExtension(FileName);
        if (_iconKey != iconKey)
        {
            _iconKey = iconKey;
            SetFileIcon(null);
            _ = LoadFileIconAsync(FileName, null, iconKey);
        }

        if (first) NotifyPackageLayout();
    }

    /// <summary>A line under a torrent's files: the rest of a long list, or why there is no list yet.</summary>
    internal void UpdateTorrentNote(Guid rowId, Guid parentId, string text, bool more, bool problem)
    {
        var first = !IsTorrentFile;
        IsTorrentFile = true;
        IsTorrentMore = more;
        Id = rowId;
        TorrentParentId = parentId;
        FileName = text;
        State = "torrentFile";
        StateText = string.Empty;
        StatusBrush = NeutralStatus;
        TotalSize = 0;
        SizeText = string.Empty;
        RowToolTip = more ? "Progress Details lists every file" : null;
        DetailText = string.Empty;
        Description = string.Empty;
        FileGlyph = more ? "" : problem ? "" : "";
        if (first) NotifyPackageLayout();
    }

    /// <summary>Shows this row as a package header summarising <paramref name="children"/>.</summary>
    public void UpdatePackage(DownloadGroupDto group, IReadOnlyList<JobViewModel> children, bool expanded)
    {
        Id = group.Id;
        IsPackage = true;
        StatusNote = null;
        IsExpanded = expanded;
        Children = children;
        var dtos = children.Where(child => child.Dto is not null).Select(child => child.Dto!).ToList();
        var summary = DownloadGroupSummary.Create(dtos);
        FileName = group.Name;
        Url = string.Empty;
        State = summary.State == "active" ? "downloading" : summary.State;
        var word = State == "downloading" && children.Any(child => child.IsProviderFile) ? "Downloading locally" : ToDisplayState(State);
        StateText = $"{word} · {summary.CompletedCount}/{summary.MemberCount}";
        TotalSize = summary.TotalBytes ?? summary.ReceivedBytes;
        BytesPerSecond = summary.BytesPerSecond;
        SizeText = TotalSize > 0 ? UiFormat.Bytes(TotalSize) : string.Empty;
        SpeedText = BytesPerSecond > 0 ? $"{UiFormat.Bytes((long)BytesPerSecond)}/s" : string.Empty;
        Remaining = summary.EstimatedRemaining;
        EtaText = DurationText.TimeLeft(summary.EstimatedRemaining);
        ProgressPercent = summary.Progress is { } progress ? Math.Clamp(progress * 100, 0, 100) : 0;
        ProgressVisibility = summary.State is "active" or "paused" && ProgressPercent > 0 ? Visibility.Visible : Visibility.Collapsed;
        CanPause = PackageRules.PauseTargets(dtos).Count > 0;
        CanResume = PackageRules.ResumeTargets(group.Id, dtos).Count > 0;
        CanCancelPackage = PackageRules.CancelTargets(dtos).Count > 0;
        IsTerminal = children.All(child => child.IsTerminal);
        DetailText = $"{summary.MemberCount} files";
        Description = $"{summary.MemberCount} files";
        RawDescription = string.Empty;
        QueueText = PackageRules.QueueText(dtos);
        SharedQueueId = PackageRules.SharedQueueId(dtos);
        QueueName = QueueText.Length == 0 ? "None"
            : QueueText == PackageRules.MixedQueueMarker ? "Files are in different queues"
            : dtos[0].QueueName ?? $"Queue {dtos[0].QueueId}";
        LastUpdated = children.Count == 0 ? DateTimeOffset.MinValue : children.Max(child => child.LastUpdated);
        CreatedAt = children.Count == 0 ? DateTimeOffset.MinValue : children.Min(child => child.CreatedAt);
        LastTryText = children.Count == 0 ? string.Empty
            : UiFormat.Moment(LastUpdated);
        CategoryId = children.FirstOrDefault()?.CategoryId ?? "other";
        Destination = children.FirstOrDefault()?.Destination ?? string.Empty;
        StatusBrush = State switch
        {
            "downloading" => ActiveStatus,
            "completed" => DoneStatus,
            "failed" => FailedStatus,
            "paused" or "queued" => WaitingStatus,
            _ => NeutralStatus,
        };
    }

    // ---- Remote rows: work on a provider's side, with no local transfer behind them yet ----

    private string? _rowToolTip;
    private string? _accessibleName;
    private Thickness _rowBorder;

    /// <summary>A row for a remote job. It never joins the local job list, its counts or its bulk actions.</summary>
    public bool IsRemoteRow { get; private set; }

    /// <summary>The last remote job applied to this row; null for any other row.</summary>
    public RemoteJobDto? RemoteJob { get; private set; }

    /// <summary>What the Host says about this download beyond its state; null for any other row.</summary>
    public string? StatusNote { get; private set; }

    /// <summary>Finished, and its archive went on purpose after extraction. Nothing is wrong with it, and there is no file to act on.</summary>
    public bool FileRemovedAfterExtraction => !IsSideRow && !IsPackage && Dto?.FileRemovedAfterExtraction == true;

    /// <summary>A local file a provider supplies. Its address is a reference, not something to edit or send again.</summary>
    public bool IsProviderFile => !IsRemoteRow && (Dto?.IsRemote == true || Url.StartsWith("remote://", StringComparison.OrdinalIgnoreCase));

    public string? RowToolTip { get => _rowToolTip; private set => Set(ref _rowToolTip, value); }

    /// <summary>The line under the last remote row, which sets them apart from the local rows below.</summary>
    public Thickness RowBorder { get => _rowBorder; private set => Set(ref _rowBorder, value); }

    public Windows.UI.Text.FontStyle NameStyle => IsRemoteRow ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

    public void MarkLastRemoteRow(bool last) => RowBorder = last ? new Thickness(0, 0, 0, 1) : default;

    /// <summary>
    /// Shows a remote job. Size, speed and time left stay blank because those columns are about bytes
    /// reaching this computer, and none are yet. No bar either: the stage is said in words.
    /// </summary>
    internal void UpdateRemote(RemoteJobDto job, RemoteRowView view)
    {
        var first = !IsRemoteRow;
        IsRemoteRow = true;
        RemoteJob = job;
        Id = job.Id;
        FileName = view.Name;
        State = "remote";
        StateText = view.Status;
        DetailText = view.Detail;
        Description = view.Detail;
        RowToolTip = view.ToolTip;
        _accessibleName = view.AccessibleName;
        FileGlyph = "";
        CreatedAt = job.CreatedAt;
        LastUpdated = job.UpdatedAt;
        LastTryText = UiFormat.Moment(job.UpdatedAt);
        StatusBrush = ToneBrush(view.Tone);
        if (first) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameStyle)));
    }

    internal static Microsoft.UI.Xaml.Media.Brush ToneBrush(RemoteRowTone tone) => tone switch
    {
        RemoteRowTone.Failed => FailedStatus,
        RemoteRowTone.Waiting => WaitingStatus,
        RemoteRowTone.Active => ActiveStatus,
        RemoteRowTone.Done => DoneStatus,
        _ => NeutralStatus,
    };

    // ---- The Extraction column, and rows for archive records with no download on the list ----

    private string _archiveText = string.Empty;
    private string? _archiveToolTip;
    private string? _archiveAccessibleText;
    private Microsoft.UI.Xaml.Media.Brush _archiveBrush = NeutralStatus;
    private Visibility _archivePasswordVisibility = Visibility.Collapsed;

    /// <summary>Whether the Host answers <c>archive.*</c>. With no extraction to speak of, the column takes no room.</summary>
    internal static bool ShowExtractionColumn { get; set; }

    public Visibility ExtractionColumnVisibility => ShowExtractionColumn ? Visibility.Visible : Visibility.Collapsed;

    internal void NotifyExtractionColumn() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExtractionColumnVisibility)));

    /// <summary>The state of this file's or package's archive, kept apart from <see cref="StateText"/>: a failed extraction is no failed download.</summary>
    public string ArchiveText { get => _archiveText; private set => Set(ref _archiveText, value); }

    public string? ArchiveToolTip { get => _archiveToolTip; private set => Set(ref _archiveToolTip, value); }

    public Microsoft.UI.Xaml.Media.Brush ArchiveBrush { get => _archiveBrush; private set => Set(ref _archiveBrush, value); }

    /// <summary>The row's own action when its archive waits for a password.</summary>
    public Visibility ArchivePasswordVisibility { get => _archivePasswordVisibility; private set => Set(ref _archivePasswordVisibility, value); }

    /// <summary>A row for an archive record whose files aren't on the list, such as one picked from disk.</summary>
    public bool IsArchiveRow { get; private set; }

    /// <summary>The set behind an archive record's own row; null for any other row.</summary>
    public ArchiveSetDto? ArchiveSet { get; private set; }

    /// <summary>A row with no local download behind it. It never counts as one, and download commands skip it.</summary>
    public bool IsSideRow => IsRemoteRow || IsArchiveRow || IsTorrentFile;

    internal void ShowArchive(ArchiveRowView? view)
    {
        ArchiveText = view?.Text ?? string.Empty;
        ArchiveToolTip = view?.ToolTip;
        ArchiveBrush = ToneBrush(view?.Tone ?? RemoteRowTone.Neutral);
        ArchivePasswordVisibility = view?.NeedsPassword == true ? Visibility.Visible : Visibility.Collapsed;
        _archiveAccessibleText = view?.AccessibleText;
    }

    /// <summary>Status stays blank: nothing is being downloaded, and the Extraction column says the rest.</summary>
    internal void UpdateArchiveRow(ArchiveSetDto set, ArchiveRowView view)
    {
        IsArchiveRow = true;
        ArchiveSet = set;
        Id = set.Id;
        FileName = set.Name;
        State = "archive";
        DetailText = view.ToolTip;
        Description = DownloadManager.Desktop.WinUI.ArchiveText.Full(set);
        Destination = set.Directory;
        FileGlyph = "";
        CreatedAt = set.CreatedAt;
        LastUpdated = set.UpdatedAt;
        LastTryText = UiFormat.Moment(set.UpdatedAt);
        ShowArchive(view);
    }

    /// <summary>Says which account a provider's file came from, when its row has nothing else to say.</summary>
    public void ShowProviderSource(string words)
    {
        if (RawDescription.Length == 0) Description = JobNotes.Description(StatusNote, words);
    }

    /// <summary>What a screen reader says for this row.</summary>
    public override string ToString()
    {
        if (_accessibleName is not null) return _accessibleName;
        var torrent = IsTorrent && Dto?.Torrent is { } shown
            ? $"torrent, {TorrentRules.FilesSummary(shown)}{(HasChevron ? IsExpanded ? ", files shown" : ", files hidden" : string.Empty)}"
            : string.Empty;
        var parts = new[] { FileName, torrent, StateText, SizeText, EtaText.Length > 0 ? $"{EtaText} left" : string.Empty, SpeedText, StatusNote ?? string.Empty, _archiveAccessibleText ?? string.Empty };
        return string.Join(", ", parts.Where(part => part.Length > 0));
    }

    private async Task LoadFileIconAsync(string fileName, string? completedPath, string key)
    {
        try
        {
            var image = await FileIconProvider.LoadAsync(fileName, completedPath, key, OnAnotherServer);
            if (_iconKey == key) SetFileIcon(image);
        }
        catch (Exception ex)
        {
            // Icon lookup must never interrupt job updates or surface an unobserved task error.
            UiTrace.Write($"File icon unavailable: {ex.GetType().Name}");
        }
    }

    private void SetFileIcon(ImageSource? image)
    {
        FileIcon = image;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileIconVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileGlyphVisibility)));
    }

    private static string GlyphFor(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "zip" or "7z" or "rar" or "gz" or "gzip" or "bz2" or "tar" or "xz" or "zst" => "\uE7C3",
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "txt" or "rtf" or "epub" => "\uE8A5",
            "mp3" or "flac" or "wav" or "m4a" or "aac" or "ogg" or "wma" or "opus" => "\uE8D6",
            "mp4" or "mkv" or "avi" or "mov" or "wmv" or "webm" or "m4v" or "mpg" or "mpeg" => "\uE714",
            "exe" or "msi" or "apk" or "msix" or "appx" or "bat" or "cmd" => "\uE756",
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "svg" or "ico" => "\uEB9F",
            "iso" or "img" => "\uE7B8",
            _ => "\uE7C3",
        };
    }

    internal static string ToDisplayState(string state)
        => state switch
        {
            "queued" => "Queued",
            "connecting" => "Connecting",
            "downloading" => "Downloading",
            "retryWaiting" => "Retrying",
            "verifying" => "Verifying",
            "paused" => "Paused",
            "completed" => "Completed",
            "failed" => "Failed",
            "cancelled" => "Cancelled",
            _ => state,
        };

    private static string ToDisplaySource(string source)
        => source switch
        {
            "chromeContextMenu" => "Browser menu",
            "chromeAuto" => "Browser",
            "clipboard" => "Clipboard",
            "cli" => "CLI",
            "batch" or "cliBatch" => "Batch",
            // An import, from the app or the CLI ("cliImport"), has always been shown as App.
            _ => "App",
        };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
