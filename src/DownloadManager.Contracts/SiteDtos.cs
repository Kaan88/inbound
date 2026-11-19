namespace DownloadManager.Contracts;

/// <summary>
/// Sites (SITES.2, docs/plans/remote-sites.md): the switch, saved sites, and the SFTP host-key trust. Answered for the user
/// at this PC only (the desktop role without a web scope: the window and the CLI); a web session is refused until the
/// owner settles whether it may (remote-sites.md, open question 2). With Sites off every request but <see cref="Get"/>
/// and <see cref="SetEnabled"/> is refused with "Sites is off" and rclone never starts.
/// <para>Secrets go in (<see cref="SiteSaveRequest"/>) and never come back out: a <see cref="SiteDto"/> says only whether
/// one is stored.</para>
/// </summary>
public static class SitesMessageTypes
{
    /// <summary><c>{}</c>; answered with <see cref="Status"/>.</summary>
    public const string Get = "sites.get";

    /// <summary><see cref="SitesSetEnabledRequest"/>; answered with <see cref="Status"/>.</summary>
    public const string SetEnabled = "sites.setEnabled";

    /// <summary><c>{}</c>; answered with <see cref="List"/>.</summary>
    public const string ListRequest = "sites.list";

    /// <summary><see cref="SiteSaveRequest"/>; answered with <see cref="Result"/> (its <c>Site</c> set on success).</summary>
    public const string Save = "sites.save";

    /// <summary><see cref="SiteIdRequest"/>; answered with <see cref="Result"/>.</summary>
    public const string Remove = "sites.remove";

    /// <summary><c>{}</c>; answered with <see cref="Kinds"/>.</summary>
    public const string KindsRequest = "sites.kinds";

    /// <summary>
    /// <see cref="SiteProbeRequest"/>: reads an SSH server's host keys for the first-use prompt, with no login; answered
    /// with <see cref="ProbeResult"/>.
    /// </summary>
    public const string Probe = "sites.probe";

    /// <summary><see cref="SiteTrustRequest"/>: the host keys the user accepted for a site; answered with <see cref="Result"/>.</summary>
    public const string Trust = "sites.trust";

    /// <summary><see cref="SiteIdRequest"/>: connects through rclone and lists the start folder; answered with <see cref="Result"/>.</summary>
    public const string Test = "sites.test";

    /// <summary>
    /// SITES.6b, <see cref="SiteIdRequest"/>: reads the certificate an FTPS or https site's server shows, with a TLS
    /// handshake and no login; answered with <see cref="CertificateResult"/>.
    /// </summary>
    public const string Certificate = "sites.certificate";

    /// <summary>The reply to <see cref="Certificate"/>: <see cref="SiteCertificateResultDto"/>.</summary>
    public const string CertificateResult = "sites.certificateResult";

    /// <summary>
    /// SITES.6c, <see cref="SiteSignInRequest"/>: starts a browser sign-in (one at a time) or takes a pasted token; answered
    /// with <see cref="SignInResult"/>. The site changes (<see cref="Changed"/>) when the sign-in ends.
    /// </summary>
    public const string SignIn = "sites.signIn";

    /// <summary><see cref="SiteIdRequest"/>: stops a browser sign-in that is waiting; answered with <see cref="Result"/>.</summary>
    public const string SignInCancel = "sites.signInCancel";

    public const string SignInResult = "sites.signInResult";

    /// <summary>
    /// SITES.7, "to this device": <see cref="SiteDeviceOpenRequest"/> with <c>path</c>, a file; answered with
    /// <see cref="DeviceStream"/>. Then <see cref="DeviceRead"/> until <c>done</c>. The server's web front streams it to
    /// the browser; a stream unused for two minutes is closed.
    /// </summary>
    public const string DeviceReadOpen = "sites.device.readOpen";

    /// <summary>SITES.7, "from this device": <see cref="SiteDeviceOpenRequest"/> with <c>folder</c>, <c>name</c> and <c>size</c>.</summary>
    public const string DeviceWriteOpen = "sites.device.writeOpen";

    /// <summary><see cref="SiteDeviceChunkRequest"/>: the next piece of a read stream, answered with <see cref="DeviceChunk"/>.</summary>
    public const string DeviceRead = "sites.device.read";

    /// <summary><see cref="SiteDeviceChunkRequest"/> with <c>data</c>: a piece of a write stream; <c>last</c> ends it.</summary>
    public const string DeviceWrite = "sites.device.write";

    /// <summary><see cref="SiteDeviceChunkRequest"/>: stops a stream (an upload is not kept); answered with <see cref="Result"/>.</summary>
    public const string DeviceClose = "sites.device.close";

    public const string DeviceStream = "sites.device.stream";
    public const string DeviceChunk = "sites.device.chunk";

    /// <summary>
    /// <see cref="SitesRcloneInstallRequest"/>: downloads, checks and installs rclone (Options › Sites › Download rclone, or
    /// Update rclone); answered with <see cref="Status"/> when done. Not for "Use my own rclone", which Inbound never touches.
    /// </summary>
    public const string RcloneInstall = "sites.rclone.install";

    /// <summary><c>{}</c>: asks rclone's server for a newer release now; answered with <see cref="Status"/>.</summary>
    public const string RcloneCheck = "sites.rclone.check";

    /// <summary>
    /// <see cref="SiteBrowseRequest"/>: one folder of a site, sorted folders first then by name, paged; answered with
    /// <see cref="BrowseResult"/> (SITES.3). Read-only. A host-key problem comes back as in <see cref="Test"/>.
    /// </summary>
    public const string Browse = "sites.browse";

    /// <summary><see cref="SiteStarRequest"/>: stars or unstars a folder of a site; answered with <see cref="Result"/> (its <c>Site</c> set).</summary>
    public const string Star = "sites.star";

    /// <summary>
    /// <see cref="SiteTransferStartRequest"/>: a download or upload batch (SITES.4). Stored before anything moves; answered
    /// with <see cref="TransferResult"/> (its <c>Transfer</c> set).
    /// </summary>
    public const string TransferStart = "sites.transfer.start";
    public const string SafeDownloadStart = "sites.transfer.safeDownload";

    /// <summary><see cref="SiteTransferIdRequest"/>: stops the batch's files in flight; answered with <see cref="TransferResult"/>.</summary>
    public const string TransferPause = "sites.transfer.pause";

    /// <summary><see cref="SiteTransferIdRequest"/>: a paused batch goes on; files that were cut start again from zero.</summary>
    public const string TransferResume = "sites.transfer.resume";

    /// <summary><see cref="SiteTransferIdRequest"/>: stops the batch for good; files already done stay.</summary>
    public const string TransferCancel = "sites.transfer.cancel";

    /// <summary><see cref="SiteTransferIdRequest"/>: the batch's failed files are tried again.</summary>
    public const string TransferRetry = "sites.transfer.retry";

    /// <summary><see cref="SiteTransferIdRequest"/>: removes a finished batch from the list, or every finished batch when the id is empty.</summary>
    public const string TransferClear = "sites.transfer.clear";

    /// <summary><c>{}</c>: the batches; answered with <see cref="TransferListResult"/>. Answered with Sites off too.</summary>
    public const string TransferList = "sites.transfer.list";

    /// <summary><see cref="SiteTransferIdRequest"/>: one batch with its files; answered with <see cref="TransferResult"/> (its <c>Items</c> set).</summary>
    public const string TransferGet = "sites.transfer.get";

    public const string Status = "sites.status";
    public const string List = "sites.listResult";
    public const string Result = "sites.result";
    public const string Kinds = "sites.kindsResult";
    public const string ProbeResult = "sites.probeResult";
    public const string BrowseResult = "sites.browseResult";
    public const string TransferResult = "sites.transfer.result";
    public const string TransferListResult = "sites.transfer.listResult";

    /// <summary>
    /// Host push to the desktop role's sessions: a <see cref="SiteTransfersDto"/> with every batch, at most about twice a
    /// second while files move, and after each change of state.
    /// </summary>
    public const string TransferChanged = "sites.transfer.changed";

    /// <summary>Host push to the desktop role's sessions: a <see cref="SitesChangedDto"/> after the switch or a site changed.</summary>
    public const string Changed = "sites.changed";

    /// <summary>SITES.5: <see cref="SiteMakeFolderRequest"/>; replies <see cref="Result"/>.</summary>
    public const string MakeFolder = "sites.mkdir";

    /// <summary>SITES.5: <see cref="SiteRenameRequest"/>; replies <see cref="Result"/>.</summary>
    public const string Rename = "sites.rename";

    /// <summary>SITES.5: <see cref="SiteChangeRequest"/>; replies <see cref="CountResult"/> with what a delete would remove.</summary>
    public const string Count = "sites.count";

    /// <summary>SITES.5: <see cref="SiteChangeRequest"/>; replies <see cref="ChangeResult"/>, a result per item.</summary>
    public const string Delete = "sites.delete";

    public const string CountResult = "sites.countResult";
    public const string ChangeResult = "sites.changeResult";

    /// <summary>The requests (not the replies or the push).</summary>
    public static bool IsRequest(string? type) =>
        type is Get or SetEnabled or ListRequest or Save or Remove or KindsRequest or Probe or Trust or Test or RcloneInstall or RcloneCheck or Browse or Star
            or TransferStart or SafeDownloadStart or TransferPause or TransferResume or TransferCancel or TransferRetry or TransferClear or TransferList or TransferGet
            or MakeFolder or Rename or Count or Delete or Certificate or SignIn or SignInCancel
            or DeviceReadOpen or DeviceWriteOpen or DeviceRead or DeviceWrite or DeviceClose;
}

/// <summary>The values of <see cref="SitesStatusDto.Rclone"/>.</summary>
public static class SitesRcloneStates
{
    public const string Off = "off";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopping = "stopping";
    public const string Waiting = "waiting";
    public const string Failed = "failed";

    /// <summary>No rclone was found (not downloaded yet, or a wrong own path): Sites shows one screen saying so.</summary>
    public const string Missing = "missing";
}

public sealed class SitesStatusDto
{
    /// <summary>Options › Sites › "Use Sites".</summary>
    public bool Enabled { get; set; }

    /// <summary>"Use my own rclone": its path, or null for the bundled one.</summary>
    public string? OwnRclonePath { get; set; }

    /// <summary><see cref="SitesRcloneStates"/>.</summary>
    public string Rclone { get; set; } = SitesRcloneStates.Off;

    /// <summary>The rclone in use's version (e.g. <c>1.76.0</c>), once read; null before the first start or when missing.</summary>
    public string? RcloneVersion { get; set; }

    /// <summary>Why rclone is missing or failed, or why the last request was refused. Null otherwise.</summary>
    public string? Error { get; set; }

    /// <summary>Where the rclone in use comes from: <see cref="SitesRcloneSources"/>; null when none was found.</summary>
    public string? RcloneSource { get; set; }

    /// <summary>The version Inbound downloaded and uses (it may not have started yet); null when none is downloaded.</summary>
    public string? RcloneInstalled { get; set; }

    /// <summary>Options › Sites › "Update rclone automatically" (on by default).</summary>
    public bool AutoUpdateRclone { get; set; } = true;

    /// <summary>True while rclone is being downloaded and checked.</summary>
    public bool RcloneInstalling { get; set; }

    /// <summary>A newer rclone found by the last check, or null.</summary>
    public SitesRcloneUpdateDto? RcloneUpdate { get; set; }

    /// <summary>When rclone's server was last asked, and what went wrong if it failed.</summary>
    public DateTimeOffset? RcloneLastCheck { get; set; }

    public string? RcloneUpdateError { get; set; }

    /// <summary>Options › Sites › "Transfers at once": files moving at the same time across all sites (1 to 10, default 3).</summary>
    public int MaxTransfers { get; set; } = 3;
}

/// <summary>The values of <see cref="SitesStatusDto.RcloneSource"/>.</summary>
public static class SitesRcloneSources
{
    /// <summary>"Use my own rclone": never updated by Inbound.</summary>
    public const string Own = "own";

    /// <summary>INBOUND_RCLONE (tests, development builds).</summary>
    public const string Environment = "environment";

    /// <summary>Downloaded by Inbound into its data folder; updated by Inbound.</summary>
    public const string Downloaded = "downloaded";

    /// <summary>Shipped with this build (the Docker image); a newer downloaded one replaces it.</summary>
    public const string Shipped = "shipped";
}

/// <summary>A newer rclone: <c>ready</c> (installs by itself when automatic updates are on), <c>waiting</c> (out less than a week), or <c>needsAsk</c> (a new major version).</summary>
public sealed class SitesRcloneUpdateDto
{
    public string Version { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset? WaitUntil { get; set; }
    public string Note { get; set; } = "";
}

public sealed class SitesRcloneInstallRequest
{
    /// <summary>A version (e.g. <c>1.77.0</c>), or null for the best one: the newest a week old of the same major version, else the tested one.</summary>
    public string? Version { get; set; }
}

public sealed class SitesSetEnabledRequest
{
    public bool Enabled { get; set; }

    /// <summary>Set to change "Use my own rclone"; an empty string goes back to the one Inbound finds itself; null leaves it.</summary>
    public string? OwnRclonePath { get; set; }

    /// <summary>"Update rclone automatically"; null leaves it.</summary>
    public bool? AutoUpdateRclone { get; set; }

    /// <summary>"Transfers at once" (1 to 10); null leaves it.</summary>
    public int? MaxTransfers { get; set; }
}

/// <summary>A saved site as clients see it: never a secret, only whether one is stored.</summary>
public sealed class SiteDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";

    /// <summary><c>password</c> or <c>key</c>.</summary>
    public string AuthMethod { get; set; } = "password";

    public bool HasPassword { get; set; }
    public bool HasPrivateKey { get; set; }
    public bool HasKeyPassphrase { get; set; }

    /// <summary>The accepted host keys' fingerprints (<c>SHA256:...</c>), with their types; empty until the user accepts one.</summary>
    public List<SiteHostKeyDto> HostKeys { get; set; } = [];

    public string RemoteFolder { get; set; } = "";
    public string? LocalFolder { get; set; }

    /// <summary>Starred folders, absolute paths on the server (<c>/srv/pub</c>), in the order they were starred.</summary>
    public List<string> Stars { get; set; } = [];

    /// <summary>SITES.6: FTP only: <see cref="SiteTlsModes"/> (<c>explicit</c>, <c>implicit</c> or <c>none</c>).</summary>
    public string? Tls { get; set; }

    /// <summary>SITES.6: WebDAV only: the server's address, http or https, with no user name or password in it.</summary>
    public string? Url { get; set; }

    /// <summary>SITES.6: WebDAV only: rclone's <c>vendor</c> (<c>other</c>, <c>nextcloud</c>, <c>owncloud</c>).</summary>
    public string? Vendor { get; set; }

    /// <summary>SITES.6: S3 only: rclone's <c>provider</c> (<c>AWS</c>, <c>Cloudflare</c>, <c>Wasabi</c>, <c>Minio</c>, <c>Other</c>).</summary>
    public string? Provider { get; set; }

    /// <summary>SITES.6: S3 only: the service's address; required except for Amazon S3.</summary>
    public string? Endpoint { get; set; }

    /// <summary>SITES.6: S3 only: the region, or null for the provider's own default.</summary>
    public string? Region { get; set; }

    /// <summary>SITES.6: the kind's <see cref="SiteCapabilities"/>, so a client hides what the site can't do.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>SITES.6: false for plain FTP and for an http:// WebDAV or S3 address; the window shows "Not encrypted".</summary>
    public bool Encrypted { get; set; } = true;

    /// <summary>SITES.6c, sign-in kinds: the site holds a token (it may still have to be renewed by signing in again).</summary>
    public bool SignedIn { get; set; }

    /// <summary>SITES.6c: the user's own app registration, or null for rclone's shared one.</summary>
    public string? ClientId { get; set; }

    public bool HasClientSecret { get; set; }

    /// <summary>SITES.6c: a browser sign-in of this site is waiting on the Host; <c>sites.changed</c> follows when it ends.</summary>
    public bool SigningIn { get; set; }

    /// <summary>SITES.6b: the server certificates this site trusts beyond the system's (FTPS and https sites).</summary>
    public List<SiteTrustedCertificateDto> TrustedCertificates { get; set; } = [];
}

public sealed class SiteHostKeyDto
{
    public string Type { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    /// <summary>The <c>host_keys</c> entry (<c>type base64</c>): public, sent back unchanged in <see cref="SiteTrustRequest"/>.</summary>
    public string Entry { get; set; } = "";
}

public sealed class SitesListDto
{
    public List<SiteDto> Sites { get; set; } = [];
}

/// <summary>
/// A new site (no <see cref="Id"/>) or an edit. A secret field that is null keeps the stored value; an empty string
/// removes it. Changing the host or the port forgets the accepted host keys: the new server is asked about again.
/// </summary>
public sealed class SiteSaveRequest
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "sftp";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string AuthMethod { get; set; } = "password";
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
    public string? KeyPassphrase { get; set; }
    public string RemoteFolder { get; set; } = "";
    public string? LocalFolder { get; set; }

    /// <summary>SITES.6: FTP only: <see cref="SiteTlsModes"/> (<c>explicit</c>, <c>implicit</c> or <c>none</c>).</summary>
    public string? Tls { get; set; }

    /// <summary>SITES.6: WebDAV only: the server's address, http or https, with no user name or password in it.</summary>
    public string? Url { get; set; }

    /// <summary>SITES.6: WebDAV only: rclone's <c>vendor</c> (<c>other</c>, <c>nextcloud</c>, <c>owncloud</c>).</summary>
    public string? Vendor { get; set; }

    /// <summary>SITES.6: S3 only: rclone's <c>provider</c> (<c>AWS</c>, <c>Cloudflare</c>, <c>Wasabi</c>, <c>Minio</c>, <c>Other</c>).</summary>
    public string? Provider { get; set; }

    /// <summary>SITES.6: S3 only: the service's address; required except for Amazon S3.</summary>
    public string? Endpoint { get; set; }

    /// <summary>SITES.6: S3 only: the region, or null for the provider's own default.</summary>
    public string? Region { get; set; }

    /// <summary>SITES.6c, sign-in kinds: the user's own app registration, or null for rclone's shared one.</summary>
    public string? ClientId { get; set; }

    /// <summary>SITES.6c: the secret of <see cref="ClientId"/>; null keeps the stored one, empty removes it.</summary>
    public string? ClientSecret { get; set; }
}

/// <summary>
/// SITES.6c, <c>sites.signIn</c>: starts the browser sign-in of a Drive, OneDrive or Dropbox site, or, with
/// <see cref="Token"/>, takes the token <c>rclone authorize</c> printed on another computer (the paste flow).
/// </summary>
public sealed class SiteSignInRequest
{
    public string Id { get; set; } = "";
    public string? Token { get; set; }
}

/// <summary>SITES.7: opens a stream between a site and the device the web UI runs on.</summary>
public sealed class SiteDeviceOpenRequest
{
    public string Id { get; set; } = "";

    /// <summary>Read: the file on the server.</summary>
    public string? Path { get; set; }

    /// <summary>Write: the folder on the server, the new file's name and its size in bytes.</summary>
    public string? Folder { get; set; }

    public string? Name { get; set; }
    public long Size { get; set; }

    /// <summary>Write: replaces a file of that name; otherwise the answer is <see cref="SiteProblems.Exists"/>.</summary>
    public bool Replace { get; set; }
}

public sealed class SiteDeviceStreamDto
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Problem { get; set; }
    public string? StreamId { get; set; }
    public string? Name { get; set; }

    /// <summary>The file's size, or -1 when the server did not say.</summary>
    public long Size { get; set; } = -1;
}

public sealed class SiteDeviceChunkRequest
{
    public string StreamId { get; set; } = "";

    /// <summary>Write only: the bytes (base64 on the wire), at most <see cref="SiteDeviceLimits.ChunkBytes"/>.</summary>
    public byte[]? Data { get; set; }

    /// <summary>Write only: the last piece; the answer comes when the server has the whole file.</summary>
    public bool Last { get; set; }
}

public sealed class SiteDeviceChunkDto
{
    public bool Ok { get; set; }
    public string? Message { get; set; }

    /// <summary>Read: the next bytes (empty at the end).</summary>
    public byte[]? Data { get; set; }

    /// <summary>Read: no more bytes. Write: the file is on the server.</summary>
    public bool Done { get; set; }
}

public static class SiteDeviceLimits
{
    /// <summary>One piece: 1 MiB, well inside a frame (<see cref="Protocol.MaxFrameBytes"/>) as base64.</summary>
    public const int ChunkBytes = 1024 * 1024;

    /// <summary>Streams open at once on a Host.</summary>
    public const int MostStreams = 8;
}

public sealed class SiteSignInResultDto
{
    public bool Ok { get; set; }
    public string? Message { get; set; }

    /// <summary>The address to open in a browser on the Host's computer; the site becomes signed in when it is done.</summary>
    public string? Url { get; set; }

    /// <summary>The <c>rclone authorize</c> command for the paste flow, for a browser on another computer.</summary>
    public string? AuthorizeCommand { get; set; }

    public SiteDto? Site { get; set; }
}

public sealed class SiteIdRequest
{
    public string Id { get; set; } = "";
}

public sealed class SiteProbeRequest
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
}

public sealed class SiteProbeResultDto
{
    public bool Ok { get; set; }
    public string? Message { get; set; }

    /// <summary>Every host key the server offers, ed25519 first.</summary>
    public List<SiteHostKeyDto> Keys { get; set; } = [];
}

public sealed class SiteTrustRequest
{
    public string Id { get; set; } = "";

    /// <summary>The <see cref="SiteHostKeyDto.Entry"/> values the user accepted. Replaces the stored list.</summary>
    public List<string> Entries { get; set; } = [];

    /// <summary>
    /// SITES.6b, FTPS and https sites: the fingerprint of the certificate the user trusted, from
    /// <see cref="SiteCertificateResultDto"/>. The Host reads the certificate again and refuses when the server now shows
    /// another one. Empty with <see cref="ForgetCertificates"/> to stop trusting the site's certificates.
    /// </summary>
    public string? CertificateFingerprint { get; set; }

    public bool ForgetCertificates { get; set; }
}

/// <summary>SITES.6b: a server's certificate, for the "Trust this certificate?" question.</summary>
public sealed class SiteCertificateDto
{
    public string Subject { get; set; } = "";
    public string Issuer { get; set; } = "";
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }

    /// <summary>SHA-256, <c>AB:CD:…</c>.</summary>
    public string Fingerprint { get; set; } = "";

    public List<string> Names { get; set; } = [];
    public bool SelfSigned { get; set; }

    /// <summary>This PC trusts it as it is; there is nothing to ask.</summary>
    public bool TrustedBySystem { get; set; }

    /// <summary>Why trusting it would not help (wrong name, out of date), or null.</summary>
    public string? CannotTrust { get; set; }
}

public sealed class SiteCertificateResultDto
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public SiteCertificateDto? Certificate { get; set; }

    /// <summary>The site already trusts this certificate.</summary>
    public bool TrustedBySite { get; set; }
}

/// <summary>SITES.6b: a certificate a site trusts.</summary>
public sealed class SiteTrustedCertificateDto
{
    public string Fingerprint { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTimeOffset NotAfter { get; set; }
}

/// <summary>The values of <see cref="SiteResultDto.Problem"/>.</summary>
public static class SiteProblems
{
    /// <summary>The server's key is not the accepted one: the site stops with a hard warning until the key is replaced.</summary>
    public const string HostKeyChanged = "hostKeyChanged";

    /// <summary>No host key has been accepted yet: the window probes and asks first.</summary>
    public const string HostKeyNotAccepted = "hostKeyNotAccepted";

    /// <summary>SITES.6b: the server's TLS certificate is not trusted; the window reads it (<c>sites.certificate</c>) and asks.</summary>
    public const string CertificateNotTrusted = "certificateNotTrusted";

    /// <summary>SITES.6c: a sign-in kind with no token, or one the provider no longer accepts (<c>invalid_grant</c>): sign in again.</summary>
    public const string SignInNeeded = "signInNeeded";

    /// <summary>SITES.7: an upload from this device would replace a file of that name; it is sent again with <c>replace</c>.</summary>
    public const string Exists = "exists";

    /// <summary>Sites is off.</summary>
    public const string Off = "off";

    /// <summary>
    /// <c>sites.browse</c>: the listing a later page asked for is gone (over a minute old, another folder was listed, or the
    /// site changed). The client reads the folder again from offset 0.
    /// </summary>
    public const string ListingChanged = "listingChanged";
}

public sealed class SiteResultDto
{
    public bool Ok { get; set; }
    public string? Message { get; set; }

    /// <summary>A <see cref="SiteProblems"/> value when the failure needs its own screen; null otherwise.</summary>
    public string? Problem { get; set; }

    /// <summary>For <see cref="SiteProblems.HostKeyChanged"/>: what the server offers now, and what was accepted.</summary>
    public string? OfferedFingerprint { get; set; }

    public List<string> AcceptedFingerprints { get; set; } = [];

    /// <summary>The site after a save or a trust.</summary>
    public SiteDto? Site { get; set; }
}

/// <summary>One kind's tile and fields, so every client draws the same Add a site dialog.</summary>
public sealed class SiteKindDto
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public int DefaultPort { get; set; }
    public List<string> AuthMethods { get; set; } = [];

    /// <summary>SITES.6: the <see cref="SiteFields"/> the dialog shows, in order.</summary>
    public List<string> Fields { get; set; } = [];

    /// <summary>SITES.6: the choices of a field with a fixed set of values (FTP's TLS, WebDAV's vendor, S3's provider).</summary>
    public Dictionary<string, List<SiteChoiceDto>> Choices { get; set; } = [];

    /// <summary>SITES.6: <see cref="SiteCapabilities"/> values.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>SITES.6: the user and password boxes' own words, when not "User name" and "Password".</summary>
    public string? UserLabel { get; set; }

    public string? PasswordLabel { get; set; }

    /// <summary>SITES.6: a line the dialog shows under the fields, or null.</summary>
    public string? Note { get; set; }
}

public sealed class SiteChoiceDto
{
    public string Value { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class SiteKindsDto
{
    public List<SiteKindDto> Kinds { get; set; } = [];
}

public sealed class SitesChangedDto
{
    public SitesStatusDto Status { get; set; } = new();
    public List<SiteDto> Sites { get; set; } = [];
}

/// <summary>
/// One folder of a site. <see cref="Path"/> is absolute on the server, with <c>/</c> separators (<c>/srv/pub</c>); null or
/// empty is the site's start folder (its remote folder, else <c>/</c>). <c>..</c> and <c>.</c> segments are refused.
/// </summary>
public sealed class SiteBrowseRequest
{
    public string Id { get; set; } = "";
    public string? Path { get; set; }

    /// <summary>The first item to return, in the sorted order.</summary>
    public int Offset { get; set; }

    /// <summary>How many items at most: 1 to 2000, default 500.</summary>
    public int Limit { get; set; } = 500;

    /// <summary>Lists the folder again instead of paging a listing the Host read in the last minute.</summary>
    public bool Refresh { get; set; }

    /// <summary>
    /// For a page after the first: the <see cref="SiteBrowseResultDto.Listing"/> of the first page. When the Host no longer
    /// has that listing the reply is <see cref="SiteProblems.ListingChanged"/>, and the client starts again at offset 0.
    /// </summary>
    public string? Listing { get; set; }
}

public sealed class SiteItemDto
{
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }

    /// <summary>Bytes; -1 when the server does not say (and for folders).</summary>
    public long Size { get; set; } = -1;

    public DateTimeOffset? Modified { get; set; }

    /// <summary>
    /// SITES.5, "On this PC": a <see cref="SiteHereStates"/> value comparing the item with the same place under the
    /// site's linked folder; null when it is not compared (no linked folder, or a folder outside the site's start folder).
    /// </summary>
    public string? Here { get; set; }
}

/// <summary>The values of <see cref="SiteItemDto.Here"/>. Name, size and time only.</summary>
public static class SiteHereStates
{
    /// <summary>Same size, and not older here.</summary>
    public const string Same = "same";

    /// <summary>A copy here that is older than the server's.</summary>
    public const string OlderHere = "olderHere";

    /// <summary>A smaller file here, as from a copy that was cut.</summary>
    public const string SomeOfIt = "someOfIt";

    /// <summary>Nothing of that name here.</summary>
    public const string NotHere = "notHere";

    /// <summary>A file here that is bigger or newer and not the same size, or a folder where the server has a file.</summary>
    public const string Different = "different";

    /// <summary>A folder of that name is here; what is inside is not compared.</summary>
    public const string FolderHere = "folderHere";
}

public sealed class SiteBrowseResultDto
{
    public bool Ok { get; set; }
    public string? Message { get; set; }

    /// <summary>As in <see cref="SiteResultDto.Problem"/>.</summary>
    public string? Problem { get; set; }

    public string? OfferedFingerprint { get; set; }
    public List<string> AcceptedFingerprints { get; set; } = [];

    /// <summary>The folder listed, normalised: absolute, no trailing slash except for <c>/</c>.</summary>
    public string Path { get; set; } = "/";

    public List<SiteItemDto> Items { get; set; } = [];

    /// <summary>All items in the folder; more pages follow while <c>Offset + Items.Count &lt; Total</c>.</summary>
    public int Total { get; set; }

    public int Offset { get; set; }

    /// <summary>Names the listing these items come from; sent back in <see cref="SiteBrowseRequest.Listing"/> for the next page.</summary>
    public string? Listing { get; set; }
}

/// <summary><c>sites.mkdir</c>: a folder named <see cref="Name"/> in <see cref="Path"/>.</summary>
public sealed class SiteMakeFolderRequest
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "/";
    public string Name { get; set; } = "";
}

/// <summary><c>sites.rename</c>: the item <see cref="Name"/> in <see cref="Path"/> becomes <see cref="NewName"/>, in the same folder.</summary>
public sealed class SiteRenameRequest
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "/";
    public string Name { get; set; } = "";
    public string NewName { get; set; } = "";
}

/// <summary><c>sites.count</c> and <c>sites.delete</c>: items of one folder by name.</summary>
public sealed class SiteChangeRequest
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "/";
    public List<SiteChangeItemDto> Items { get; set; } = [];
}

public sealed class SiteChangeItemDto
{
    public string Name { get; set; } = "";

    /// <summary>What the client showed; the Host looks again before it acts.</summary>
    public bool IsFolder { get; set; }
}

/// <summary>What a delete of those items would remove.</summary>
public sealed class SiteCountResultDto
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Problem { get; set; }

    /// <summary>Files, counting those inside the chosen folders.</summary>
    public long Files { get; set; }

    /// <summary>Chosen folders (not the folders inside them).</summary>
    public int Folders { get; set; }

    public long Bytes { get; set; }

    /// <summary>Some file's size is not known, so <see cref="Bytes"/> is at least that much.</summary>
    public bool SizeUnknown { get; set; }

    /// <summary>Chosen items no longer there.</summary>
    public int Missing { get; set; }
}

public sealed class SiteChangeResultDto
{
    /// <summary>False when any item failed, or the whole request did (then <see cref="Items"/> is empty).</summary>
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Problem { get; set; }
    public List<SiteChangeItemResultDto> Items { get; set; } = [];
}

public sealed class SiteChangeItemResultDto
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string? Message { get; set; }
}

public sealed class SiteStarRequest
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Starred { get; set; } = true;
}
