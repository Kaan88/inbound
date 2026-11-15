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

    public const string Status = "sites.status";
    public const string List = "sites.listResult";
    public const string Result = "sites.result";
    public const string Kinds = "sites.kindsResult";
    public const string ProbeResult = "sites.probeResult";

    /// <summary>Host push to the desktop role's sessions: a <see cref="SitesChangedDto"/> after the switch or a site changed.</summary>
    public const string Changed = "sites.changed";

    /// <summary>The requests (not the replies or the push).</summary>
    public static bool IsRequest(string? type) =>
        type is Get or SetEnabled or ListRequest or Save or Remove or KindsRequest or Probe or Trust or Test;
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

    /// <summary>No rclone was found (unticked in Setup, or a wrong own path): Sites shows one screen with Repair.</summary>
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
}

public sealed class SitesSetEnabledRequest
{
    public bool Enabled { get; set; }

    /// <summary>Set to change "Use my own rclone"; an empty string goes back to the bundled one; null leaves it.</summary>
    public string? OwnRclonePath { get; set; }
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
}

/// <summary>The values of <see cref="SiteResultDto.Problem"/>.</summary>
public static class SiteProblems
{
    /// <summary>The server's key is not the accepted one: the site stops with a hard warning until the key is replaced.</summary>
    public const string HostKeyChanged = "hostKeyChanged";

    /// <summary>No host key has been accepted yet: the window probes and asks first.</summary>
    public const string HostKeyNotAccepted = "hostKeyNotAccepted";

    /// <summary>Sites is off.</summary>
    public const string Off = "off";
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
