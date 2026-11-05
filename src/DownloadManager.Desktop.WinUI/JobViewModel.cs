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
    public ImageSource? FileIcon { get => _fileIcon; private set => Set(ref _fileIcon, value); }
    public Visibility FileIconVisibility => FileIcon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FileGlyphVisibility => FileIcon is null ? Visibility.Visible : Visibility.Collapsed;
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
    public string QueueName { get; private set; } = "Main";

    public string FileName { get => _fileName; private set => Set(ref _fileName, value); }
    public string Url { get => _url; private set => Set(ref _url, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
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
    public double BytesPerSecond { get; private set; }
    public DateTimeOffset LastUpdated { get; private set; }
    public Visibility ProgressVisibility { get => _progressVisibility; private set => Set(ref _progressVisibility, value); }
    public DateTimeOffset CreatedAt { get => _createdAt; private set => Set(ref _createdAt, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(JobDto job)
    {
        Id = job.Id;
        FileName = job.FileName;
        Url = job.Url;
        State = job.State;
        StateText = job.QueueRetryAt is not null ? "Queue retry scheduled" : ToDisplayState(job.State);
        TotalSize = job.TotalBytes is > 0 ? job.TotalBytes.Value : job.ReceivedBytes;
        BytesPerSecond = job.BytesPerSecond ?? 0;
        LastUpdated = job.UpdatedAt;
        SizeText = FormatBytes(TotalSize);
        SpeedText = BytesPerSecond > 0 ? $"{FormatBytes((long)BytesPerSecond)}/s" : string.Empty;
        EtaText = FormatDuration(job.EstimatedRemaining);
        DetailText = job.Failure?.Message is { Length: > 0 } failure
            ? failure
            : job.Url;
        ProgressPercent = job.Progress is not null
            ? Math.Clamp(job.Progress.Value * 100, 0, 100)
            : job.TotalBytes is > 0
                ? Math.Clamp(job.ReceivedBytes * 100.0 / job.TotalBytes.Value, 0, 100)
                : 0;
        CanPause = job.CanPause;
        CanResume = job.CanResume;
        IsTerminal = job.IsTerminal;
        SourceText = ToDisplaySource(job.Source);
        Destination = job.DestinationDirectory;
        FinalFilePath = job.FinalFilePath;
        FileGlyph = GlyphFor(FileName);
        var completedPath = job.State == "completed" ? job.FinalFilePath : null;
        var iconKey = completedPath is null ? "type:" + Path.GetExtension(job.FileName)
            : $"file:{job.Id}:{completedPath}:{job.CompletedAt:O}";
        if (_iconKey != iconKey)
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
        LastTryText = (job.CompletedAt ?? job.UpdatedAt).LocalDateTime
            .ToString("MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture);
        RawDescription = job.Description ?? string.Empty;
        Description = job.Description ?? job.PageUrl ?? string.Empty;
        ProgressVisibility = (job.State is "downloading" or "connecting" or "retryWaiting" or "verifying" or "paused") &&
                             ProgressPercent > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CreatedAt = job.CreatedAt;
    }

    private async Task LoadFileIconAsync(string fileName, string? completedPath, string key)
    {
        try
        {
            var image = await FileIconProvider.LoadAsync(fileName, completedPath, key);
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
            "chromeContextMenu" => "Chrome menu",
            "chromeAuto" => "Chrome",
            "clipboard" => "Clipboard",
            "cli" => "CLI",
            "batch" => "Batch",
            _ => "App",
        };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0 ? $"{bytes} {units[index]}" : $"{value:0.0} {units[index]}";
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null || duration <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        return duration.Value.TotalHours >= 1
            ? $"{(int)duration.Value.TotalHours}h {duration.Value.Minutes}m"
            : $"{Math.Max(1, duration.Value.Minutes)}m {duration.Value.Seconds}s";
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
