namespace DownloadManager.Core.Providers;

/// <summary>
/// Supplies the address for a job whose file lives on a provider. The engine knows nothing about
/// providers; the service asks this once per run, and again when the engine reports a dead link.
/// </summary>
public interface IRemoteLinkBroker
{
    /// <summary>
    /// Gets a fresh address and checks the file is still the one the job expects. Called before
    /// the engine and inside the run's cancellation. Retry temporary provider failures in here,
    /// with backoff, and report a failure only once they have run out.
    /// </summary>
    Task<RemoteLinkResolution> PrepareRunAsync(DownloadJob job, CancellationToken cancellationToken);

    /// <summary>Whether a failed transfer is worth another try with a fresh address.</summary>
    Task<bool> ShouldRefreshAfterFailureAsync(DownloadJob job, JobFailure failure, CancellationToken cancellationToken);
}

/// <summary>A broker's answer: an address, or why there is none. The address is a secret, so this is not a record.</summary>
public sealed class RemoteLinkResolution
{
    private RemoteLinkResolution(string? url, JobFailure? failure, bool resetLocalData)
    {
        Url = url;
        Failure = failure;
        ResetLocalData = resetLocalData;
    }

    /// <summary>Held only in memory for this run.</summary>
    public string? Url { get; }
    public JobFailure? Failure { get; }

    /// <summary>The file is no longer the one the local data belongs to: delete the part and checkpoint first.</summary>
    public bool ResetLocalData { get; }
    public long? ExpectedTotalBytes { get; private init; }
    public string? ExpectedMd5 { get; private init; }
    /// <summary>The provider's limit for one file. Applied at every run, so a lowered limit reaches saved jobs.</summary>
    public int? MaxConnections { get; private init; }

    public static RemoteLinkResolution Link(
        string url,
        bool resetLocalData = false,
        long? expectedTotalBytes = null,
        string? expectedMd5 = null,
        int? maxConnections = null)
        => new(url, null, resetLocalData)
        {
            ExpectedTotalBytes = expectedTotalBytes,
            ExpectedMd5 = expectedMd5,
            MaxConnections = maxConnections,
        };

    public static RemoteLinkResolution Fail(JobFailure failure, bool resetLocalData = false)
        => new(null, failure, resetLocalData);

    public override string ToString() => Failure is null ? "[link]" : $"[failed: {Failure.Kind}]";
}
