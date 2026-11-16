using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What a Sites call came to: the Host's answer, or words for the user when none came.</summary>
internal sealed record SitesReply<T>(T? Value, string? Error) where T : class
{
    public bool Reached => Value is not null;
}

// The Sites commands of the Host (sites.*, remote-sites.md). The Host answers them to this PC's desktop role only, so a
// client that follows another server must use a client of this PC's Host (the Options window does).
internal sealed partial class RemoteClient
{
    /// <summary>The switch, the rclone state or a saved site changed on the Host. Raised on the UI thread.</summary>
    public event Action<SitesChangedDto>? SitesChanged;

    private static readonly TimeSpan SitesTestTime = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan SitesBrowseTime = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SitesInstallTime = TimeSpan.FromMinutes(10);

    public Task<SitesReply<SitesStatusDto>> GetSitesAsync(CancellationToken cancellationToken = default)
        => SitesQuickAsync<EmptyPayload, SitesStatusDto>(SitesMessageTypes.Get, new EmptyPayload(), SitesMessageTypes.Status, cancellationToken);

    public Task<SitesReply<SitesStatusDto>> SetSitesAsync(SitesSetEnabledRequest request, CancellationToken cancellationToken = default)
        => SitesQuickAsync<SitesSetEnabledRequest, SitesStatusDto>(SitesMessageTypes.SetEnabled, request, SitesMessageTypes.Status, cancellationToken);

    public Task<SitesReply<SitesStatusDto>> CheckRcloneAsync(CancellationToken cancellationToken = default)
        => SitesSlowAsync<EmptyPayload, SitesStatusDto>(SitesMessageTypes.RcloneCheck, new EmptyPayload(), SitesMessageTypes.Status, SitesTestTime, cancellationToken);

    /// <summary>Downloads and installs rclone; <paramref name="version"/> null is the best one. Returns when it is done.</summary>
    public Task<SitesReply<SitesStatusDto>> InstallRcloneAsync(string? version, CancellationToken cancellationToken = default)
        => SitesSlowAsync<SitesRcloneInstallRequest, SitesStatusDto>(SitesMessageTypes.RcloneInstall,
            new SitesRcloneInstallRequest { Version = version }, SitesMessageTypes.Status, SitesInstallTime, cancellationToken);

    public Task<SitesReply<SitesListDto>> ListSitesAsync(CancellationToken cancellationToken = default)
        => SitesQuickAsync<EmptyPayload, SitesListDto>(SitesMessageTypes.ListRequest, new EmptyPayload(), SitesMessageTypes.List, cancellationToken);

    public Task<SitesReply<SiteKindsDto>> GetSiteKindsAsync(CancellationToken cancellationToken = default)
        => SitesQuickAsync<EmptyPayload, SiteKindsDto>(SitesMessageTypes.KindsRequest, new EmptyPayload(), SitesMessageTypes.Kinds, cancellationToken);

    /// <summary>The secrets in <paramref name="request"/> go into this one message and are not kept here.</summary>
    public async Task<SiteResultDto> SaveSiteAsync(SiteSaveRequest request, CancellationToken cancellationToken = default)
        => SiteResult(await SitesQuickAsync<SiteSaveRequest, SiteResultDto>(SitesMessageTypes.Save, request, SitesMessageTypes.Result, cancellationToken).ConfigureAwait(false));

    public async Task<SiteResultDto> RemoveSiteAsync(string id, CancellationToken cancellationToken = default)
        => SiteResult(await SitesQuickAsync<SiteIdRequest, SiteResultDto>(SitesMessageTypes.Remove, new SiteIdRequest { Id = id }, SitesMessageTypes.Result, cancellationToken).ConfigureAwait(false));

    public async Task<SiteResultDto> TrustSiteAsync(string id, IEnumerable<string> entries, CancellationToken cancellationToken = default)
        => SiteResult(await SitesQuickAsync<SiteTrustRequest, SiteResultDto>(SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = id, Entries = entries.ToList() }, SitesMessageTypes.Result, cancellationToken).ConfigureAwait(false));

    /// <summary>SITES.6b: trusts the certificate with this fingerprint (the Host reads it again first), or with null forgets the site's.</summary>
    public async Task<SiteResultDto> TrustSiteCertificateAsync(string id, string? fingerprint, CancellationToken cancellationToken = default)
        => SiteResult(await SitesSlowAsync<SiteTrustRequest, SiteResultDto>(SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = id, CertificateFingerprint = fingerprint, ForgetCertificates = fingerprint is null }, SitesMessageTypes.Result,
            SitesTestTime, cancellationToken).ConfigureAwait(false));

    /// <summary>SITES.6b: the certificate the site's server shows, read by the Host with no login.</summary>
    public async Task<SiteCertificateResultDto> SiteCertificateAsync(string id, CancellationToken cancellationToken = default)
    {
        var reply = await SitesSlowAsync<SiteIdRequest, SiteCertificateResultDto>(SitesMessageTypes.Certificate,
            new SiteIdRequest { Id = id }, SitesMessageTypes.CertificateResult, SitesTestTime, cancellationToken).ConfigureAwait(false);
        return reply.Value ?? new SiteCertificateResultDto { Ok = false, Message = RemoteText.Failure(reply.Error) };
    }

    /// <summary>SITES.6c: starts a browser sign-in (the answer has the address to open), or, with a token, stores a pasted one.</summary>
    public async Task<SiteSignInResultDto> SignInSiteAsync(string id, string? token = null, CancellationToken cancellationToken = default)
    {
        var reply = await SitesSlowAsync<SiteSignInRequest, SiteSignInResultDto>(SitesMessageTypes.SignIn,
            new SiteSignInRequest { Id = id, Token = token }, SitesMessageTypes.SignInResult, SitesTestTime, cancellationToken).ConfigureAwait(false);
        return reply.Value ?? new SiteSignInResultDto { Ok = false, Message = RemoteText.Failure(reply.Error) };
    }

    public async Task<SiteResultDto> CancelSiteSignInAsync(string id, CancellationToken cancellationToken = default)
        => SiteResult(await SitesQuickAsync<SiteIdRequest, SiteResultDto>(SitesMessageTypes.SignInCancel, new SiteIdRequest { Id = id }, SitesMessageTypes.Result, cancellationToken).ConfigureAwait(false));

    public async Task<SiteResultDto> TestSiteAsync(string id, CancellationToken cancellationToken = default)
        => SiteResult(await SitesSlowAsync<SiteIdRequest, SiteResultDto>(SitesMessageTypes.Test, new SiteIdRequest { Id = id }, SitesMessageTypes.Result, SitesTestTime, cancellationToken).ConfigureAwait(false));

    public async Task<SiteProbeResultDto> ProbeSiteAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var reply = await SitesSlowAsync<SiteProbeRequest, SiteProbeResultDto>(SitesMessageTypes.Probe,
            new SiteProbeRequest { Host = host, Port = port }, SitesMessageTypes.ProbeResult, SitesTestTime, cancellationToken).ConfigureAwait(false);
        return reply.Value ?? new SiteProbeResultDto { Ok = false, Message = RemoteText.Failure(reply.Error) };
    }

    /// <summary>
    /// One page of a folder (SITES.3), on a connection of its own: a far server or a big folder can take a while, and
    /// cancelling <paramref name="cancellationToken"/> closes that connection, which ends the listing on the Host.
    /// </summary>
    public async Task<SiteBrowseResultDto> BrowseSiteAsync(SiteBrowseRequest request, CancellationToken cancellationToken = default)
    {
        var reply = await SitesSlowAsync<SiteBrowseRequest, SiteBrowseResultDto>(SitesMessageTypes.Browse, request,
            SitesMessageTypes.BrowseResult, SitesBrowseTime, cancellationToken).ConfigureAwait(false);
        return reply.Value ?? new SiteBrowseResultDto { Ok = false, Message = RemoteText.Failure(reply.Error) };
    }

    /// <summary>Stars or unstars a folder; the answer's <c>Site</c> has the new list of stars.</summary>
    public async Task<SiteResultDto> StarSiteFolderAsync(SiteStarRequest request, CancellationToken cancellationToken = default)
        => SiteResult(await SitesQuickAsync<SiteStarRequest, SiteResultDto>(SitesMessageTypes.Star, request, SitesMessageTypes.Result, cancellationToken).ConfigureAwait(false));

    /// <summary>SITES.5: a new folder on the server. Slow: the Host looks first, then makes it.</summary>
    public async Task<SiteResultDto> MakeSiteFolderAsync(SiteMakeFolderRequest request, CancellationToken cancellationToken = default)
        => SiteResult(await SitesSlowAsync<SiteMakeFolderRequest, SiteResultDto>(SitesMessageTypes.MakeFolder, request,
            SitesMessageTypes.Result, SitesTestTime, cancellationToken).ConfigureAwait(false));

    /// <summary>SITES.5: renames one item in its folder.</summary>
    public async Task<SiteResultDto> RenameSiteItemAsync(SiteRenameRequest request, CancellationToken cancellationToken = default)
        => SiteResult(await SitesSlowAsync<SiteRenameRequest, SiteResultDto>(SitesMessageTypes.Rename, request,
            SitesMessageTypes.Result, SitesBrowseTime, cancellationToken).ConfigureAwait(false));

    /// <summary>SITES.5: what a delete would remove, counted on the server (folders are walked, so it can take a while).</summary>
    public async Task<SiteCountResultDto> CountSiteItemsAsync(SiteChangeRequest request, CancellationToken cancellationToken = default)
    {
        var reply = await SitesSlowAsync<SiteChangeRequest, SiteCountResultDto>(SitesMessageTypes.Count, request,
            SitesMessageTypes.CountResult, SitesBrowseTime, cancellationToken).ConfigureAwait(false);
        return reply.Value ?? new SiteCountResultDto { Ok = false, Message = RemoteText.Failure(reply.Error) };
    }

    /// <summary>SITES.5: deletes on the server, with a result per item.</summary>
    public async Task<SiteChangeResultDto> DeleteSiteItemsAsync(SiteChangeRequest request, CancellationToken cancellationToken = default)
    {
        var reply = await SitesSlowAsync<SiteChangeRequest, SiteChangeResultDto>(SitesMessageTypes.Delete, request,
            SitesMessageTypes.ChangeResult, SitesBrowseTime, cancellationToken).ConfigureAwait(false);
        return reply.Value ?? new SiteChangeResultDto { Ok = false, Message = RemoteText.Failure(reply.Error) };
    }

    /// <summary>
    /// SITES.4: starts a download or upload batch. On a connection of its own, as browsing is: the Host lists the chosen
    /// folders first, which can take a while on a far server.
    /// </summary>
    public async Task<SiteTransferResultDto> StartSiteTransferAsync(SiteTransferStartRequest request, CancellationToken cancellationToken = default)
        => TransferResult(await SitesSlowAsync<SiteTransferStartRequest, SiteTransferResultDto>(SitesMessageTypes.TransferStart, request,
            SitesMessageTypes.TransferResult, SitesBrowseTime, cancellationToken).ConfigureAwait(false));

    /// <summary>Pause, resume, cancel, retry or clear (<see cref="SitesMessageTypes"/>) one batch; clear with an empty id
    /// removes every finished one.</summary>
    public async Task<SiteTransferResultDto> SiteTransferActionAsync(string type, string id, CancellationToken cancellationToken = default)
    {
        if (!SiteTransferRules.IsAction(type)) throw new ArgumentException("Not a transfer action.", nameof(type));
        // A pause or cancel waits for the files in flight to stop (up to 15 s each on the Host).
        return TransferResult(await SitesSlowAsync<SiteTransferIdRequest, SiteTransferResultDto>(type, new SiteTransferIdRequest { Id = id },
            SitesMessageTypes.TransferResult, SitesTestTime, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>One batch with its files.</summary>
    public async Task<SiteTransferResultDto> GetSiteTransferAsync(string id, CancellationToken cancellationToken = default)
        => TransferResult(await SitesQuickAsync<SiteTransferIdRequest, SiteTransferResultDto>(SitesMessageTypes.TransferGet,
            new SiteTransferIdRequest { Id = id }, SitesMessageTypes.TransferResult, cancellationToken).ConfigureAwait(false));

    private static SiteTransferResultDto TransferResult(SitesReply<SiteTransferResultDto> reply)
        => reply.Value ?? new SiteTransferResultDto { Ok = false, Message = RemoteText.Failure(reply.Error) };

    private static SiteResultDto SiteResult(SitesReply<SiteResultDto> reply)
        => reply.Value ?? new SiteResultDto { Ok = false, Message = RemoteText.Failure(reply.Error) };

    private async Task<SitesReply<TResult>> SitesQuickAsync<TRequest, TResult>(string type, TRequest request, string replyType, CancellationToken cancellationToken)
        where TResult : class
    {
        string? failure = null;
        var value = await QuickAsync<TRequest, TResult?>(type, request, replyType, error => { failure = error; return null; }, cancellationToken).ConfigureAwait(false);
        return new SitesReply<TResult>(value, value is null ? failure ?? RemoteText.HostUnreadable : null);
    }

    private async Task<SitesReply<TResult>> SitesSlowAsync<TRequest, TResult>(string type, TRequest request, string replyType, TimeSpan timeout, CancellationToken cancellationToken)
        where TResult : class
    {
        string? failure = null;
        var value = await SlowAsync<TRequest, TResult?>(type, request, replyType, error => { failure = error; return null; }, cancellationToken, timeout).ConfigureAwait(false);
        return new SitesReply<TResult>(value, value is null ? failure ?? RemoteText.HostUnreadable : null);
    }

    private void RaiseSitesChanged(IpcMessage message)
    {
        var change = message.GetPayload<SitesChangedDto>();
        _post(() => SitesChanged?.Invoke(change));
    }
}
