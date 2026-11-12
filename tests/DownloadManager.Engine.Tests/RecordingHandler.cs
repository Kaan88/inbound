using System.Collections.Concurrent;
using System.Net;
using DownloadManager.Core;

namespace DownloadManager.Engine.Tests;

/// <summary>
/// Passes requests to a real loopback handler and records what the engine actually sent, so a test can tell a
/// resume from a full re-download and see which headers crossed to another origin.
/// </summary>
internal sealed class RecordingHandler : DelegatingHandler
{
    public RecordingHandler()
        : base(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
        })
    {
    }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue(new RecordedRequest(request.RequestUri!, request.Headers.Range?.ToString(), headers));
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>Range headers the transfer sent, without the segmented path's one-byte probe.</summary>
    public IReadOnlyList<string?> TransferRanges()
        => Requests.Select(r => r.Range).Where(range => range != "bytes=0-0").ToList();

    /// <summary>Each requested range's first byte; a request with no Range starts at 0.</summary>
    public IReadOnlyList<long> TransferStarts()
        => TransferRanges().Select(range => range is null ? 0L : long.Parse(range["bytes=".Length..].Split('-')[0])).ToList();

    /// <summary>
    /// Fails when the resumed run asked for a byte the checkpoint already counted: every request must start past
    /// the completed part of the segment it falls in.
    /// </summary>
    public void AssertResumedFrom(DownloadCheckpoint checkpoint)
    {
        var starts = TransferStarts();
        Assert.NotEmpty(starts);
        foreach (var start in starts)
        {
            foreach (var segment in checkpoint.Segments)
            {
                var done = segment.Start + segment.Completed;
                Assert.False(start >= segment.Start && start < done,
                    $"The resume asked again for byte {start}, which the checkpoint had ({segment.Start}-{done - 1}).");
            }
        }
    }
}

internal sealed record RecordedRequest(Uri Uri, string? Range, IReadOnlyDictionary<string, string> Headers);
