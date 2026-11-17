using System.ComponentModel;
using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>How a row is coloured: what the state means to the person, not the engine's word for it.</summary>
public enum StatusKind { Active, Done, Failed, Waiting, Neutral }

/// <summary>Which state view a row belongs to (the filter chips of the Downloads screen).</summary>
public enum RowGroup { Active, Queued, Paused, Finished, Failed }

/// <summary>What the button at the end of a row does.</summary>
public enum RowAction { None, Pause, Resume, Retry, ShowInFolder }

/// <summary>One row of the Downloads screen. Raises PropertyChanged on the UI thread only (the model posts).</summary>
public sealed class JobRow : INotifyPropertyChanged
{
    private string _subtitle = "";
    private StatusKind _kind;
    private double _progress;
    private bool _showBar;
    private bool _indeterminate;

    public Guid Id { get; init; }

    private RowGroup _group = RowGroup.Queued;
    private RowAction _action = RowAction.None;

    /// <summary>The state view of the row; changes when the job changes state.</summary>
    public RowGroup Group { get => _group; private set => Set(ref _group, value, nameof(Group)); }

    /// <summary>What the row's end button does now.</summary>
    public RowAction Action { get => _action; private set => Set(ref _action, value, nameof(Action)); }

    /// <summary>The file name the Host knows, empty until it does (<see cref="Name"/> falls back to the address).</summary>
    public string FileName { get; private set; } = "";

    /// <summary>The job as the Host last sent it (the Details screen reads what a row does not carry).</summary>
    public JobDto Job { get; private set; } = new();

    /// <summary>When the job was added; the list is newest first.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Recent transfer rates in bytes/s, one sample per update while the job transfers (newest last, at most <see cref="MaxRates"/>).</summary>
    public List<float> RateHistory { get; } = new();

    public const int MaxRates = 120;

    private string _name = "";

    /// <summary>The file name, or the address until the Host knows the name; raises PropertyChanged so a rename alone reaches the row.</summary>
    public string Name { get => _name; private set => Set(ref _name, value, nameof(Name)); }

    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value, nameof(Subtitle)); }

    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value, nameof(Kind)); }

    /// <summary>0..1.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value, nameof(Progress)); }

    public bool ShowBar { get => _showBar; private set => Set(ref _showBar, value, nameof(ShowBar)); }

    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value, nameof(Indeterminate)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void ApplyFrom(JobDto j, DateTimeOffset? now = null)
    {
        Job = j;
        FileName = j.FileName ?? "";
        Name = string.IsNullOrEmpty(j.FileName) ? j.Url : j.FileName;
        CreatedAt = j.CreatedAt;
        var state = (j.State ?? "").ToLowerInvariant();
        var total = j.TotalBytes;
        var rate = j.BytesPerSecond ?? 0;
        switch (state)
        {
            case "downloading":
            case "connecting":
            case "verifying":
                RateHistory.Add((float)rate);
                if (RateHistory.Count > MaxRates) RateHistory.RemoveAt(0);
                Kind = StatusKind.Active; Group = RowGroup.Active; Action = RowAction.Pause;
                ShowBar = true;
                Indeterminate = state != "downloading" || total is null or 0;
                Progress = total is > 0 ? Math.Clamp(j.ReceivedBytes / (double)total.Value, 0, 1) : 0;
                var parts = new List<string> { total is > 0 ? $"{Fmt.Bytes(j.ReceivedBytes)} of {Fmt.Bytes(total.Value)}" : Fmt.Bytes(j.ReceivedBytes) };
                if (rate > 0) parts.Add(Fmt.Rate(rate));
                if (rate > 0 && total is > 0 && total > j.ReceivedBytes) parts.Add(Fmt.Eta((total.Value - j.ReceivedBytes) / rate));
                Subtitle = string.Join(" · ", parts);
                break;
            case "completed":
                Kind = StatusKind.Done; Group = RowGroup.Finished; ShowBar = false; Indeterminate = false; Progress = 1;
                Action = j.FileRemovedAfterExtraction ? RowAction.None : RowAction.ShowInFolder;
                Subtitle = "Finished · " + Fmt.Bytes(total ?? j.ReceivedBytes) + (j.CompletedAt is { } done ? " · " + Fmt.When(done, now ?? DateTimeOffset.Now) : "");
                break;
            case "paused":
                Kind = StatusKind.Waiting; Group = RowGroup.Paused; Action = RowAction.Resume; ShowBar = total is > 0; Indeterminate = false;
                Progress = total is > 0 ? Math.Clamp(j.ReceivedBytes / (double)total.Value, 0, 1) : 0;
                Subtitle = "Paused" + (total is > 0 ? $" · {Fmt.Bytes(j.ReceivedBytes)} of {Fmt.Bytes(total.Value)}" : "");
                break;
            case "failed":
            case "cancelled":
                Kind = StatusKind.Failed; Group = RowGroup.Failed; Action = RowAction.Retry; ShowBar = false; Indeterminate = false;
                Subtitle = (state == "failed" ? "Failed" : "Cancelled") + (FailureWords.Short(j) is { } why ? " · " + why : "");
                break;
            case "retrywaiting":
                Kind = StatusKind.Waiting; Group = RowGroup.Queued; Action = RowAction.Pause; ShowBar = false; Indeterminate = false; Subtitle = "Waiting to retry";
                break;
            default:
                Kind = StatusKind.Neutral; Group = RowGroup.Queued; Action = RowAction.Resume; ShowBar = false; Indeterminate = false;
                Subtitle = "Queued" + (total is > 0 ? " · " + Fmt.Bytes(total.Value) : "");
                break;
        }
    }
}
