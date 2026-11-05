namespace DownloadManager.Core;

/// <summary>An in-flight HTTP response stream, not a physical TCP socket (HTTP/2 may multiplex streams).</summary>
public sealed record ConnectionTelemetry(Guid Id, string Host, string State, long? RangeStart, long? RangeEnd,
    long ReceivedBytes, double BytesPerSecond, int? StatusCode, string? HttpVersion, DateTimeOffset StartedAt);

public interface IConnectionTelemetrySource
{
    IReadOnlyList<ConnectionTelemetry> GetConnections(Guid jobId);
}
