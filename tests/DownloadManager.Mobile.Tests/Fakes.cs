using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

/// <summary>A Host that records the commands it is sent and answers them with canned replies; frames are pushed by the test.</summary>
internal sealed class FakeHostConnection : IHostConnection
{
    private Action<IpcMessage>? _onFrame;

    public List<IpcMessage> Sent { get; } = new();

    public Func<IpcMessage, IpcMessage>? Reply { get; set; }

    public int Opened { get; private set; }

    public bool Disposed { get; private set; }

    public Task OpenAsync(Action<IpcMessage> onFrame, CancellationToken cancellationToken)
    {
        Opened++;
        _onFrame = onFrame;
        return Task.CompletedTask;
    }

    public Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        Sent.Add(request);
        var reply = Reply?.Invoke(request) ?? IpcMessage.Create(MessageTypes.JobAddResult, new AddJobResponse { Accepted = true }, request.Id);
        return Task.FromResult(reply);
    }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

    public void Push(IpcMessage frame) => _onFrame!(frame);

    public void Snapshot(string defaultFolder = "/dl", params JobDto[] jobs) =>
        Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Jobs = jobs.ToList(), Settings = new SettingsDto { DefaultDownloadDirectory = defaultFolder } }));

    public void Event(JobDto job, string type = "updated") =>
        Push(IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto { Job = job, EventType = type }));
}

internal static class Jobs
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public static JobDto Make(string state, string name = "file.bin", long? total = null, long received = 0, double? rate = null,
        string? note = null, int minute = 0, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Url = "http://example.test/" + name,
        FileName = name,
        State = state,
        TotalBytes = total,
        ReceivedBytes = received,
        BytesPerSecond = rate,
        StatusNote = note,
        CreatedAt = T0.AddMinutes(minute),
    };

    public static JobRow Row(JobDto job)
    {
        var row = new JobRow { Id = job.Id };
        row.ApplyFrom(job);
        return row;
    }
}
