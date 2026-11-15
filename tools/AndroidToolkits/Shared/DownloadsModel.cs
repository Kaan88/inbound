using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Ipc;

namespace AndroidTrial;

public enum StatusKind { Active, Done, Failed, Waiting, Neutral }

/// <summary>One row of the Downloads screen. Raises PropertyChanged on the UI thread only (the model posts).</summary>
public sealed class JobRow : INotifyPropertyChanged
{
    private string _subtitle = "";
    private StatusKind _kind;
    private double _progress;
    private bool _showBar;
    private bool _indeterminate;

    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value, nameof(Subtitle)); }
    public StatusKind Kind { get => _kind; set => Set(ref _kind, value, nameof(Kind)); }
    /// <summary>0..1.</summary>
    public double Progress { get => _progress; set => Set(ref _progress, value, nameof(Progress)); }
    public bool ShowBar { get => _showBar; set => Set(ref _showBar, value, nameof(ShowBar)); }
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value, nameof(Indeterminate)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void ApplyFrom(JobDto j)
    {
        Name = string.IsNullOrEmpty(j.FileName) ? j.Url : j.FileName;
        var state = (j.State ?? "").ToLowerInvariant();
        var total = j.TotalBytes;
        var rate = j.BytesPerSecond ?? 0;
        switch (state)
        {
            case "downloading":
            case "connecting":
            case "verifying":
                Kind = StatusKind.Active;
                ShowBar = true;
                Indeterminate = state != "downloading" || total is null or 0;
                Progress = total is > 0 ? Math.Clamp(j.ReceivedBytes / (double)total.Value, 0, 1) : 0;
                var parts = new List<string> { total is > 0 ? $"{Fmt.Bytes(j.ReceivedBytes)} of {Fmt.Bytes(total.Value)}" : Fmt.Bytes(j.ReceivedBytes) };
                if (rate > 0) parts.Add(Fmt.Rate(rate));
                if (rate > 0 && total is > 0 && total > j.ReceivedBytes) parts.Add(Fmt.Eta((total.Value - j.ReceivedBytes) / rate));
                Subtitle = string.Join(" · ", parts);
                break;
            case "completed":
                Kind = StatusKind.Done; ShowBar = false; Indeterminate = false; Progress = 1;
                Subtitle = "Finished · " + Fmt.Bytes(total ?? j.ReceivedBytes);
                break;
            case "paused":
                Kind = StatusKind.Waiting; ShowBar = total is > 0; Indeterminate = false;
                Progress = total is > 0 ? Math.Clamp(j.ReceivedBytes / (double)total.Value, 0, 1) : 0;
                Subtitle = "Paused" + (total is > 0 ? $" · {Fmt.Bytes(j.ReceivedBytes)} of {Fmt.Bytes(total.Value)}" : "");
                break;
            case "failed":
            case "cancelled":
                Kind = StatusKind.Failed; ShowBar = false; Indeterminate = false;
                Subtitle = (state == "failed" ? "Failed" : "Cancelled") + (string.IsNullOrEmpty(j.StatusNote) ? "" : " · " + j.StatusNote);
                break;
            case "retrywaiting":
                Kind = StatusKind.Waiting; ShowBar = false; Indeterminate = false; Subtitle = "Waiting to retry";
                break;
            default:
                Kind = StatusKind.Neutral; ShowBar = false; Indeterminate = false;
                Subtitle = "Queued" + (total is > 0 ? " · " + Fmt.Bytes(total.Value) : "");
                break;
        }
    }
}

internal static class Fmt
{
    public static string Bytes(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = b; var i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return (i == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)) + " " + u[i];
    }

    public static string Rate(double bps) => Bytes((long)bps) + "/s";

    public static string Eta(double seconds) =>
        seconds < 90 ? $"{Math.Max(1, (int)seconds)} s left" : seconds < 5400 ? $"{(int)Math.Round(seconds / 60)} min left" : $"{seconds / 3600:0.#} h left";
}

/// <summary>
/// The Downloads screen's state, fed by the in-process Host: the snapshot, then job.event frames. UI-agnostic: it only needs a way to
/// run code on the UI thread. Events are coalesced: no timer runs while nothing arrives (no ticks while idle), and while downloads
/// run the UI is told at most every 250 ms. New jobs are inserted on top; existing rows never move.
/// </summary>
public sealed class DownloadsModel
{
    private readonly Action<Action> _post;
    private readonly Dictionary<Guid, JobRow> _byId = new();
    private readonly Dictionary<Guid, (JobDto Job, string Type)> _pending = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private bool _flushScheduled;
    private bool _shown;
    private AndroidHost? _host;
    private InProcessSession? _session;

    public DownloadsModel(Action<Action> post)
    {
        _post = post;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ObservableCollection<JobRow> Rows { get; } = new();

    /// <summary>Raised on the UI thread once, after the snapshot's rows are in <see cref="Rows"/> (also for an empty snapshot).</summary>
    public event Action? FirstSnapshotShown;

    public async Task StartAsync(string stateRoot, CancellationToken ct)
    {
        _host = await AndroidHost.StartAsync(stateRoot, m => Android.Util.Log.Info("InboundTkHost", m), ct).ConfigureAwait(false);
        var session = new InProcessSession();
        _session = session;
        var gotSnapshot = false;
        session.OnFrame = m =>
        {
            if (m.Type == MessageTypes.JobsSnapshot && !gotSnapshot)
            {
                gotSnapshot = true;
                var snap = m.GetPayload<SnapshotDto>();
                _post(() => ApplySnapshot(snap));
            }
            else if (m.Type == MessageTypes.JobEvent && gotSnapshot)
            {
                var e = m.GetPayload<JobEventDto>();
                lock (_gate)
                {
                    _pending[e.Job.Id] = (e.Job, e.EventType);
                    if (_flushScheduled) return;
                    _flushScheduled = true;
                }

                _timer.Change(250, Timeout.Infinite);
            }
        };
        await _host.OpenAsync(session, ct).ConfigureAwait(false);
    }

    private void ApplySnapshot(SnapshotDto snap)
    {
        foreach (var j in snap.Jobs.OrderByDescending(x => x.CreatedAt))
        {
            var row = new JobRow { Id = j.Id };
            row.ApplyFrom(j);
            _byId[j.Id] = row;
            Rows.Add(row);
        }

        _shown = true;
        FirstSnapshotShown?.Invoke();
    }

    private void Flush()
    {
        (JobDto Job, string Type)[] batch;
        lock (_gate)
        {
            batch = _pending.Values.ToArray();
            _pending.Clear();
            _flushScheduled = false;
        }

        _post(() =>
        {
            if (!_shown) return;
            foreach (var (job, type) in batch)
            {
                if (type.Equals("removed", StringComparison.OrdinalIgnoreCase) || type.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                {
                    if (_byId.Remove(job.Id, out var gone)) Rows.Remove(gone);
                    continue;
                }

                if (_byId.TryGetValue(job.Id, out var row)) row.ApplyFrom(job);
                else
                {
                    row = new JobRow { Id = job.Id };
                    row.ApplyFrom(job);
                    _byId[job.Id] = row;
                    Rows.Insert(0, row);
                }
            }
        });
    }

    /// <summary>Seeds as the test script asks: <paramref name="count"/> jobs, <paramref name="real"/> of them real throttled downloads.</summary>
    public async Task SeedAsync(string destination, string serverBase, int count, int real, CancellationToken ct)
    {
        if (_host is null || _session is null) return;
        var idle = Math.Max(0, count - real);
        for (var i = 0; i < idle; i++)
            await AddAsync($"http://127.0.0.1:9/seed-{i}.bin", $"seed-{i}.bin", destination, "later", ct).ConfigureAwait(false);
        long[] sizes = [700_000_000, 1_100_000_000, 1_600_000_000];
        int[] bps = [2_000_000, 3_000_000, 4_000_000];
        string[] names = ["ubuntu-26.10-desktop-amd64.iso", "field-recordings-vol3.zip", "conference-talk-1080p.mp4"];
        for (var i = 0; i < real; i++)
        {
            var k = i % 3;
            await AddAsync($"{serverBase}/bench.bin?size={sizes[k]}&bps={bps[k]}&n={Guid.NewGuid():N}", names[k] + (i >= 3 ? $".{i}" : ""), destination, "startNow", ct).ConfigureAwait(false);
        }
    }

    private async Task AddAsync(string url, string fileName, string destination, string startMode, CancellationToken ct)
    {
        var add = IpcMessage.Create(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = url, DestinationDirectory = destination, FileName = fileName, Connections = 1, StartMode = startMode, Source = "androidtk", ConflictPolicy = "rename",
        });
        await _host!.RequestAsync(_session!, add, ct).ConfigureAwait(false);
    }
}
