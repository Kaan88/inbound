using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DownloadManager.Sites;

/// <summary>
/// Which rclone the Host runs and whether it is new enough (remote-sites.md, "Which binary"). Order: the path the user set
/// in Options ("Use my own rclone"), <see cref="RcloneVariable"/> (tests, development builds), the copy Inbound downloaded
/// (<see cref="RcloneUpdater"/>), then one shipped in <c>tools/rclone/</c> next to the program (the Docker image).
/// </summary>
public static partial class RcloneSetup
{
    /// <summary>An rclone elsewhere (tests, development builds).</summary>
    public const string RcloneVariable = "INBOUND_RCLONE";

    /// <summary>
    /// The oldest rclone accepted for SFTP: the first with <c>host_keys</c>, by rclone's changelog (SITES.0, point 3; 1.75.0
    /// itself was not run). An older one is refused for SFTP rather than connecting without checking the host key.
    /// </summary>
    public static readonly Version MinimumForSftp = new(1, 75, 0);

    public static string FileNameFor(bool windows) => windows ? "rclone.exe" : "rclone";

    /// <summary>The rclone to run, or null when none is there.</summary>
    public static string? Find(string? ownPath = null, string? baseDirectory = null, string? downloaded = null) => Find(baseDirectory ?? AppContext.BaseDirectory,
        ownPath, Environment.GetEnvironmentVariable(RcloneVariable), downloaded, OperatingSystem.IsWindows(), File.Exists);

    /// <summary>The same lookup with everything given (tests).</summary>
    public static string? Find(string baseDirectory, string? ownPath, string? fromEnvironment, bool windows, Func<string, bool> exists) =>
        Find(baseDirectory, ownPath, fromEnvironment, null, windows, exists);

    /// <summary>The same lookup with everything given (tests).</summary>
    public static string? Find(string baseDirectory, string? ownPath, string? fromEnvironment, string? downloaded, bool windows, Func<string, bool> exists)
    {
        // A path that was set (in Options or INBOUND_RCLONE) but is missing is not replaced by another rclone: that one
        // was asked for, and silently running a different version would hide the mistake.
        if (!string.IsNullOrWhiteSpace(ownPath)) return exists(ownPath) ? ownPath : null;
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return exists(fromEnvironment) ? fromEnvironment : null;
        if (!string.IsNullOrWhiteSpace(downloaded) && exists(downloaded)) return downloaded;
        return Shipped(baseDirectory, windows, exists);
    }

    /// <summary>The rclone shipped next to the program (the Docker image), or null.</summary>
    public static string? Shipped(string? baseDirectory = null) => Shipped(baseDirectory ?? AppContext.BaseDirectory, OperatingSystem.IsWindows(), File.Exists);

    private static string? Shipped(string baseDirectory, bool windows, Func<string, bool> exists)
    {
        var separator = windows ? '\\' : '/';
        var folder = baseDirectory.EndsWith('/') || baseDirectory.EndsWith('\\') ? baseDirectory : baseDirectory + separator;
        var candidate = folder + "tools" + separator + "rclone" + separator + FileNameFor(windows);
        return exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// The version from the first line of <c>rclone version</c>, e.g. <c>rclone v1.76.0</c>, <c>rclone v1.76.0-beta.8899.abc</c>
    /// or <c>rclone v1.76.0-DEV</c>; a pre-release counts as its release. Null when the text is not rclone's.
    /// </summary>
    public static Version? ParseVersion(string output)
    {
        var line = output.AsSpan().TrimStart();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0) line = line[..end];
        var match = VersionLine().Match(line.ToString());
        if (!match.Success) return null;
        return int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && int.TryParse(match.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
                ? new Version(major, minor, patch)
                : null;
    }

    /// <summary>Runs <c>rclone version</c> (no network, no config) and reads the version; null with a reason when it can't.</summary>
    public static async Task<(Version? Version, string? Problem)> ReadVersionAsync(string executable, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("version");
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add("");
        RcloneEnvironment.Clean(info.Environment);
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, $"rclone could not be started: {e.Message}");
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var version = ParseVersion(await stdout.ConfigureAwait(false));
                _ = await stderr.ConfigureAwait(false);
                return version is null
                    ? (null, "The file is not rclone, or its version could not be read.")
                    : (version, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return (null, "rclone did not answer within 30 seconds.");
            }
        }
    }

    /// <summary>The message for an rclone too old for SFTP, or null when it is new enough.</summary>
    public static string? TooOldForSftp(Version version) => version >= MinimumForSftp
        ? null
        : $"rclone {version.ToString(3)} is too old for SFTP remotes: {MinimumForSftp.ToString(3)} or later is needed to check the server's key.";

    [GeneratedRegex(@"^rclone v(\d+)\.(\d+)\.(\d+)(?:[-+][0-9A-Za-z.\-]*)?\s*$")]
    private static partial Regex VersionLine();
}

/// <summary>The environment every rclone the Host starts gets.</summary>
public static class RcloneEnvironment
{
    /// <summary>
    /// Removes every <c>RCLONE_*</c> variable the Host inherited: one of them (a config path, a remote, a log level, a
    /// password) would change what rclone does or logs behind the Host's back. The rc user and password are set after this.
    /// </summary>
    public static void Clean(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.ToList())
            if (key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)) environment.Remove(key);
    }
}
