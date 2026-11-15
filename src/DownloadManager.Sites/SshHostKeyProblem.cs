using System.Text.RegularExpressions;

namespace DownloadManager.Sites;

public enum SshHostKeyProblemKind
{
    /// <summary>The server offered a key of a pinned type with another fingerprint.</summary>
    Changed,

    /// <summary>The server no longer offers any pinned key type (e.g. only RSA where ed25519 was pinned).</summary>
    TypeNoLongerOffered,
}

/// <summary>
/// rclone's refusal of an SFTP server whose key does not match <c>host_keys</c>, read from its error text. Both forms seen in
/// the rclone run (SITES.0, point 3; rclone 1.76.0) mean the same to the user: the server's key is not the accepted one,
/// and the site stops with a hard warning until the key is replaced in Edit site (remote-sites.md, step 4).
/// </summary>
public sealed partial record SshHostKeyProblem(
    SshHostKeyProblemKind Kind,
    string? OfferedType,
    string? OfferedFingerprint,
    IReadOnlyList<string> PinnedFingerprints,
    IReadOnlyList<string> OfferedTypes)
{
    /// <summary>The problem in <paramref name="message"/>, or null when it is about something else.</summary>
    public static SshHostKeyProblem? Parse(string message)
    {
        var changed = Mismatch().Match(message);
        if (changed.Success)
        {
            var pinned = Fingerprint().Matches(changed.Groups["pinned"].Value).Select(m => m.Value).ToArray();
            return new SshHostKeyProblem(SshHostKeyProblemKind.Changed, changed.Groups["type"].Value, changed.Groups["offered"].Value,
                pinned, [changed.Groups["type"].Value]);
        }

        var noCommon = NoCommonAlgorithm().Match(message);
        if (noCommon.Success)
        {
            var offered = Quoted().Matches(noCommon.Groups["peer"].Value).Select(m => m.Groups[1].Value).ToArray();
            return new SshHostKeyProblem(SshHostKeyProblemKind.TypeNoLongerOffered, null, null, [], offered);
        }

        return null;
    }

    // host key mismatch for 127.0.0.1:46022: server offered ssh-ed25519 key with fingerprint SHA256:<offered>; pinned
    // fingerprints for this algorithm: SHA256:<pinned> -- if the server key has changed legitimately, ...
    [GeneratedRegex(@"host key mismatch for \S+: server offered (?<type>\S+) key with fingerprint (?<offered>SHA256:[A-Za-z0-9+/]+=*); pinned fingerprints for this algorithm: (?<pinned>[^-]*?)(?: --|$)")]
    private static partial Regex Mismatch();

    // ssh: no common algorithm for host key; we offered: ["ssh-ed25519"], peer offered: ["rsa-sha2-512" "rsa-sha2-256"]
    [GeneratedRegex(@"no common algorithm for host key; we offered: \[[^\]]*\], peer offered: \[(?<peer>[^\]]*)\]")]
    private static partial Regex NoCommonAlgorithm();

    [GeneratedRegex(@"SHA256:[A-Za-z0-9+/]+=*")]
    private static partial Regex Fingerprint();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex Quoted();
}
