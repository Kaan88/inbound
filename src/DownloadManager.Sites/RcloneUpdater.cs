using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Persistence;

namespace DownloadManager.Sites;

/// <summary>rclone's name for this OS and CPU in its archive names (<c>rclone-v1.76.0-windows-amd64.zip</c>).</summary>
public sealed record RclonePlatform(string Os, string Arch)
{
    public static RclonePlatform? Current { get; } = Detect();

    public bool Windows => Os == "windows";

    public string ArchiveName(Version version) => $"rclone-v{version.ToString(3)}-{Os}-{Arch}.zip";

    /// <summary>The binary's path inside the archive.</summary>
    public string EntryName(Version version) => $"rclone-v{version.ToString(3)}-{Os}-{Arch}/{RcloneSetup.FileNameFor(Windows)}";

    private static RclonePlatform? Detect()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : null;
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "386",
            _ => null,
        };
        return os is null || arch is null ? null : new RclonePlatform(os, arch);
    }
}

/// <summary>
/// The rclone this Inbound release was tested with (remote-sites.md, "Getting rclone"): the first download, and the floor
/// an update never goes below. Its hashes are rclone's own <c>SHA256SUMS</c> for that version, checked against the
/// signature on 2026-11-15; a download of it must match both the signed list and these.
/// </summary>
public static class RclonePinned
{
    public static readonly Version Version = new(1, 76, 0);

    public static readonly IReadOnlyDictionary<string, string> Sha256 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rclone-v1.76.0-windows-amd64.zip"] = "8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc",
        ["rclone-v1.76.0-windows-arm64.zip"] = "c406c8a69a5a408e50624aa990fbb0a6266b3a4e83782a94de3cb96f50f86f10",
        ["rclone-v1.76.0-windows-386.zip"] = "b9c8d062ff61e2addfc31c1981219d90f99e0012cba45b649f728082ad810d5c",
        ["rclone-v1.76.0-linux-amd64.zip"] = "b00f00b3266c3764413afe599d3ec316f6f43b3e0ea8a0bc1d186d2133e46df7",
        ["rclone-v1.76.0-linux-arm64.zip"] = "ee169016675e23a69d77240565133ff7dbc9b33d16fa8f7053d4af39f1eaeb78",
        ["rclone-v1.76.0-linux-386.zip"] = "ac5a696aff30c2806475d7c9ae6bbb4eeda47067e5add32448bf33db54552a09",
        ["rclone-v1.76.0-osx-amd64.zip"] = "4f5835733d52b818340f97b1fc0348a70461534d123363a183ca2b660219595a",
        ["rclone-v1.76.0-osx-arm64.zip"] = "e72dc90b70f4947a7b72b61bde4a4fbba5777b2874fb448951ffdde67ebd0ab7",
    };
}

/// <summary>
/// rclone's release files over HTTPS (remote-sites.md, "rclone updates"). The checksum list must come identical from
/// rclone's own server and from the GitHub release, and carry a good signature; a zip must match its line. Redirects are
/// followed by hand (the Host's proxied handler follows none), only over HTTPS and only to these hosts.
/// </summary>
/// <param name="verify">Checks the signed list (tests sign with a key of their own); rclone's release key by default.</param>
public sealed class RcloneDownloads(HttpClient http, Uri? primary = null, Uri? mirror = null, IReadOnlySet<string>? allowedHosts = null,
    Func<string, RcloneSignedText>? verify = null)
{
    public static readonly Uri RcloneServer = new("https://downloads.rclone.org/");
    public static readonly Uri GitHubReleases = new("https://github.com/rclone/rclone/releases/download/");

    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const int MaxSumsBytes = 256 * 1024;

    private static readonly IReadOnlySet<string> DefaultHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "downloads.rclone.org", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    private readonly Uri _primary = primary ?? RcloneServer;
    private readonly Uri _mirror = mirror ?? GitHubReleases;
    private readonly IReadOnlySet<string> _hosts = allowedHosts ?? DefaultHosts;

    /// <summary>The newest release, from <c>version.txt</c>.</summary>
    public async Task<Version> LatestAsync(CancellationToken cancellationToken)
    {
        var text = System.Text.Encoding.UTF8.GetString(await GetSmallAsync(new Uri(_primary, "version.txt"), 1024, cancellationToken).ConfigureAwait(false));
        return RcloneSetup.ParseVersion(text) ?? throw new RcloneReleaseException("rclone's version file could not be read.");
    }

    /// <summary>The verified checksum list of a release, and when it was signed.</summary>
    public async Task<(IReadOnlyDictionary<string, string> Sums, DateTimeOffset SignedAt)> SumsAsync(Version version, CancellationToken cancellationToken)
    {
        var path = $"v{version.ToString(3)}/SHA256SUMS";
        var fromRclone = GetSmallAsync(new Uri(_primary, path), MaxSumsBytes, cancellationToken);
        var fromGitHub = GetSmallAsync(new Uri(_mirror, path), MaxSumsBytes, cancellationToken);
        var (a, b) = (await fromRclone.ConfigureAwait(false), await fromGitHub.ConfigureAwait(false));
        if (!a.AsSpan().SequenceEqual(b))
            throw new RcloneReleaseException($"rclone {version.ToString(3)}'s checksum list differs between rclone's server and GitHub; it is not used.");
        var signed = (verify ?? RcloneReleaseSignature.VerifySigned)(System.Text.Encoding.UTF8.GetString(a));
        return (RcloneReleaseSignature.ParseSums(signed.Text), signed.SignedAt);
    }

    /// <summary>Downloads a release archive to <paramref name="destination"/>, which must not exist; checks its SHA-256.</summary>
    public async Task DownloadAsync(Version version, string archiveName, string sha256, string destination, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(new Uri(_primary, $"v{version.ToString(3)}/{archiveName}"), cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxArchiveBytes) throw new RcloneReleaseException("rclone's download is larger than expected.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxArchiveBytes) throw new RcloneReleaseException("rclone's download is larger than expected.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha256, StringComparison.Ordinal))
            throw new RcloneReleaseException($"The downloaded {archiveName} does not match rclone's checksum; it was not used.");
    }

    private async Task<byte[]> GetSmallAsync(Uri address, int limit, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > limit) throw new RcloneReleaseException("A file from rclone's server is larger than expected.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new RcloneReleaseException("A file from rclone's server is larger than expected.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(Uri address, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= 5; hop++)
        {
            if (address.Scheme != Uri.UriSchemeHttps || !_hosts.Contains(address.Host))
                throw new RcloneReleaseException($"rclone's download pointed at {address.Host}, which is not rclone's or GitHub's; it was not used.");
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw new RcloneReleaseException($"{address.Host} could not be reached: {e.Message}", e);
            }

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                address = location.IsAbsoluteUri ? location : new Uri(address, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new RcloneReleaseException($"{address.Host} answered {status} for {Path.GetFileName(address.AbsolutePath)}.");
            }

            return response;
        }

        throw new RcloneReleaseException("rclone's download redirected too many times.");
    }
}

/// <summary>What the update check found.</summary>
public sealed record RcloneOffer(Version Version, RcloneOfferState State, DateTimeOffset? WaitUntil = null)
{
    public string Note => State switch
    {
        RcloneOfferState.Ready => $"rclone {Version.ToString(3)} is ready to install.",
        RcloneOfferState.Waiting => $"rclone {Version.ToString(3)} is out; it is offered from {WaitUntil:yyyy-MM-dd}, a week after its release.",
        RcloneOfferState.NeedsAsk => $"rclone {Version.ToString(3)} is a new major version; it is installed only when you ask.",
        _ => "",
    };
}

public enum RcloneOfferState
{
    /// <summary>Checked, signed, a week old: installs by itself when automatic updates are on.</summary>
    Ready,

    /// <summary>Checked and signed, but released less than a week ago.</summary>
    Waiting,

    /// <summary>A new major version: installed only when the user asks.</summary>
    NeedsAsk,
}

/// <summary><c>tools/rclone/state.json</c>: which downloaded version is in use, the one before it, and versions given up on.</summary>
public sealed record RcloneInstallState
{
    public string? Current { get; init; }

    /// <summary>Kept for a rollback until the current one has run.</summary>
    public string? Previous { get; init; }

    /// <summary>True once the current version has started and answered for a real use.</summary>
    public bool Proven { get; init; }

    /// <summary>Versions that failed after installing; never offered again.</summary>
    public List<string> Refused { get; init; } = [];

    public DateTimeOffset? LastCheck { get; init; }
    public string? OfferVersion { get; init; }
    public RcloneOfferState? OfferState { get; init; }
    public DateTimeOffset? OfferWaitUntil { get; init; }
    public string? LastError { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RcloneInstallState))]
internal sealed partial class RcloneInstallJsonContext : JsonSerializerContext;

/// <summary>
/// Downloads, checks, installs and rolls back rclone in <c>&lt;state&gt;/tools/rclone/&lt;version&gt;/</c> (remote-sites.md,
/// "Getting rclone" and "rclone updates"). Per user, so no admin rights. A version becomes current only after its
/// archive matched the signed list, its binary reports that version, and it started and answered. The one before is
/// kept until the new one has run. The user's own rclone is never touched.
/// </summary>
public sealed class RcloneUpdater
{
    /// <summary>A release is offered once its signature is this old.</summary>
    public static readonly TimeSpan WaitAfterRelease = TimeSpan.FromDays(7);

    private const string StateFile = "state.json";

    private readonly string _folder;
    private readonly RcloneDownloads _downloads;
    private readonly RclonePlatform _platform;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly Func<string, string, CancellationToken, Task<string?>> _selfTest;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RcloneInstallState _state;

    /// <param name="selfTest">(binary, scratch folder) to a problem or null; starts <c>rclone rcd</c> and asks it its version by default.</param>
    public RcloneUpdater(string stateDirectory, RcloneDownloads downloads, RclonePlatform platform, Action<string> log,
        TimeProvider? time = null, Func<string, string, CancellationToken, Task<string?>>? selfTest = null)
    {
        _folder = Path.Combine(stateDirectory, "tools", "rclone");
        _downloads = downloads;
        _platform = platform;
        _log = log;
        _time = time ?? TimeProvider.System;
        _selfTest = selfTest ?? SelfTestAsync;
        _state = Load();
        RemoveLeftovers();
    }

    public string Folder => _folder;

    public RcloneInstallState State => Volatile.Read(ref _state);

    public bool Busy => _gate.CurrentCount == 0;

    /// <summary>The downloaded rclone in use, or null when none is installed.</summary>
    public string? CurrentExecutable => ExecutableFor(State.Current);

    /// <summary>The offer from the last check, if any.</summary>
    public RcloneOffer? Offer => State is { OfferVersion: { } v, OfferState: { } s } && Version.TryParse(v, out var version)
        ? new RcloneOffer(version, s, State.OfferWaitUntil)
        : null;

    /// <summary>Asks rclone's server what is newest and keeps what it found. Never installs.</summary>
    public async Task<RcloneOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RcloneOffer? offer = null;
            try
            {
                offer = await FindOfferAsync(cancellationToken).ConfigureAwait(false);
                await SaveAsync(State with
                {
                    LastCheck = _time.GetUtcNow(),
                    OfferVersion = offer?.Version.ToString(3),
                    OfferState = offer?.State,
                    OfferWaitUntil = offer?.WaitUntil,
                    LastError = null,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (RcloneReleaseException e)
            {
                _log($"rclone update check: {e.Message}");
                await SaveAsync(State with { LastCheck = _time.GetUtcNow(), LastError = e.Message }, cancellationToken).ConfigureAwait(false);
                throw;
            }

            return offer;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Installs <paramref name="version"/>, or with null the best one there is: the newest that is a week old, of the
    /// pinned major version, and not refused; else the pinned one. Returns the version now current.
    /// </summary>
    public async Task<Version> InstallAsync(Version? version, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (version is null)
            {
                Version? best = null;
                try
                {
                    var offer = await FindOfferAsync(cancellationToken, ignoreCurrent: true).ConfigureAwait(false);
                    if (offer is { State: RcloneOfferState.Ready }) best = offer.Version;
                }
                catch (RcloneReleaseException e)
                {
                    _log($"rclone: the newest release could not be checked ({e.Message}); the tested one is installed.");
                }

                version = best ?? RclonePinned.Version;
            }

            if (version < RclonePinned.Version)
                throw new RcloneReleaseException($"rclone {version.ToString(3)} is older than the {RclonePinned.Version.ToString(3)} this Inbound was tested with.");
            if (State.Refused.Contains(version.ToString(3)))
                throw new RcloneReleaseException($"rclone {version.ToString(3)} failed here before and is not installed again.");
            if (State.Current == version.ToString(3) && CurrentExecutable is not null) return version;

            await InstallCoreAsync(version, cancellationToken).ConfigureAwait(false);
            return version;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The current version started and answered for a real use: the previous one is no longer needed.</summary>
    public async Task MarkProvenAsync(CancellationToken cancellationToken = default)
    {
        if (State.Proven || State.Current is null) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State.Proven || State.Current is null) return;
            var previous = State.Previous;
            await SaveAsync(State with { Proven = true, Previous = null }, cancellationToken).ConfigureAwait(false);
            if (previous is not null) DeleteVersion(previous);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The current version failed before it ever ran: it is refused and the previous one is current again. False when
    /// there is nothing to go back to (the current one has run before, or there is no previous one).
    /// </summary>
    public async Task<bool> RollBackAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = State;
            if (state.Proven || state.Current is null) return false;
            var failed = state.Current;
            var refused = state.Refused.Contains(failed) ? state.Refused : [.. state.Refused, failed];
            var back = state.Previous is { } previous && ExecutableFor(previous) is not null ? previous : null;
            _log($"rclone {failed} failed after it was installed ({reason}); {(back is null ? "no earlier version is kept" : $"going back to {back}")}.");
            await SaveAsync(state with { Current = back, Previous = null, Proven = back is not null, Refused = refused, LastError = reason }, cancellationToken)
                .ConfigureAwait(false);
            DeleteVersion(failed);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// For an image or install that ships its own rclone (Docker): downloaded versions at or below it are removed, so the
    /// shipped one is used until a newer one has been downloaded.
    /// </summary>
    public async Task RemoveAtOrBelowAsync(Version shipped, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = State;
            bool AtOrBelow(string? v) => v is not null && Version.TryParse(v, out var parsed) && parsed <= shipped;
            if (!AtOrBelow(state.Current) && !AtOrBelow(state.Previous)) return;
            var current = AtOrBelow(state.Current) ? null : state.Current;
            var previous = AtOrBelow(state.Previous) ? null : state.Previous;
            if (AtOrBelow(state.Current)) DeleteVersion(state.Current!);
            if (AtOrBelow(state.Previous)) DeleteVersion(state.Previous!);
            await SaveAsync(state with { Current = current, Previous = previous }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RcloneOffer?> FindOfferAsync(CancellationToken cancellationToken, bool ignoreCurrent = false)
    {
        var latest = await _downloads.LatestAsync(cancellationToken).ConfigureAwait(false);
        // Only something newer than what is in use, and never at or below the tested version (which needs no offer).
        var floor = RclonePinned.Version;
        if (!ignoreCurrent && State.Current is { } c && Version.TryParse(c, out var current) && current > floor) floor = current;
        if (latest <= floor || State.Refused.Contains(latest.ToString(3))) return null;

        var (sums, signedAt) = await _downloads.SumsAsync(latest, cancellationToken).ConfigureAwait(false);
        if (!sums.ContainsKey(_platform.ArchiveName(latest))) return null; // no build for this OS and CPU
        if (latest.Major != RclonePinned.Version.Major) return new RcloneOffer(latest, RcloneOfferState.NeedsAsk);
        var ready = signedAt + WaitAfterRelease;
        return ready > _time.GetUtcNow() ? new RcloneOffer(latest, RcloneOfferState.Waiting, ready) : new RcloneOffer(latest, RcloneOfferState.Ready);
    }

    private async Task InstallCoreAsync(Version version, CancellationToken cancellationToken)
    {
        var name = version.ToString(3);
        var archive = _platform.ArchiveName(version);
        var (sums, _) = await _downloads.SumsAsync(version, cancellationToken).ConfigureAwait(false);
        if (!sums.TryGetValue(archive, out var sha256)) throw new RcloneReleaseException($"rclone {name} has no build for this PC ({_platform.Os}, {_platform.Arch}).");
        if (RclonePinned.Sha256.TryGetValue(archive, out var pinned) && pinned != sha256)
            throw new RcloneReleaseException($"rclone {name}'s checksum list does not match the one built into Inbound; it was not used.");

        Directory.CreateDirectory(_folder);
        var stamp = Guid.NewGuid().ToString("N");
        var zip = Path.Combine(_folder, $".{name}.{stamp}.zip");
        var unpacked = Path.Combine(_folder, $".{name}.{stamp}");
        var target = Path.Combine(_folder, name);
        try
        {
            _log($"rclone: downloading {archive}.");
            await _downloads.DownloadAsync(version, archive, sha256, zip, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(unpacked);
            var binary = Path.Combine(unpacked, RcloneSetup.FileNameFor(_platform.Windows));
            using (var reader = ZipFile.OpenRead(zip))
            {
                var entry = reader.GetEntry(_platform.EntryName(version))
                    ?? throw new RcloneReleaseException($"{archive} does not hold {_platform.EntryName(version)}.");
                if (entry.Length > 512L * 1024 * 1024) throw new RcloneReleaseException($"{archive} holds a binary larger than expected.");
                entry.ExtractToFile(binary);
            }

            if (!_platform.Windows)
                File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var (found, problem) = await RcloneSetup.ReadVersionAsync(binary, cancellationToken).ConfigureAwait(false);
            if (found != version) throw new RcloneReleaseException(problem ?? $"The downloaded rclone says it is {found?.ToString(3)}, not {name}.");
            var selfTest = await _selfTest(binary, Path.Combine(unpacked, "self-test"), cancellationToken).ConfigureAwait(false);
            if (selfTest is not null) throw new RcloneReleaseException($"The downloaded rclone {name} did not start: {selfTest}");
            TryDeleteDirectory(Path.Combine(unpacked, "self-test"));

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(unpacked, target);

            var state = State;
            // The version being replaced is kept for a rollback only if it has run; an unproven one is just dropped.
            var keep = state.Current is { } old && old != name && state.Proven ? old : state.Previous;
            var drop = new[] { state.Current, state.Previous }.Where(v => v is not null && v != name && v != keep).ToList();
            await SaveAsync(state with
            {
                Current = name,
                Previous = keep,
                Proven = false,
                OfferVersion = state.OfferVersion == name ? null : state.OfferVersion,
                OfferState = state.OfferVersion == name ? null : state.OfferState,
                OfferWaitUntil = state.OfferVersion == name ? null : state.OfferWaitUntil,
                LastError = null,
            }, cancellationToken).ConfigureAwait(false);
            foreach (var dropped in drop) DeleteVersion(dropped!);
            _log($"rclone {name} installed{(keep is null ? "" : $"; {keep} is kept until it has run")}.");
        }
        finally
        {
            TryDelete(zip);
            TryDeleteDirectory(unpacked);
        }
    }

    private static async Task<string?> SelfTestAsync(string binary, string scratch, CancellationToken cancellationToken)
    {
        try
        {
            await using var rclone = await RcloneProcess.StartAsync(binary, scratch, null, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await rclone.Client.CallAsync("core/version", cancellationToken: cancellationToken).ConfigureAwait(false);
            await rclone.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return null;
        }
        catch (Exception e) when (e is RcloneUnavailableException or RcloneCallException or IOException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return e.Message;
        }
    }

    private string? ExecutableFor(string? version)
    {
        if (version is null || !Version.TryParse(version, out _)) return null;
        var path = Path.Combine(_folder, version, RcloneSetup.FileNameFor(_platform.Windows));
        return File.Exists(path) ? path : null;
    }

    private void DeleteVersion(string version)
    {
        if (Version.TryParse(version, out _)) TryDeleteDirectory(Path.Combine(_folder, version));
    }

    /// <summary>A download or unpacking cut off by a crash leaves a dot-named file or folder; none is ever used.</summary>
    private void RemoveLeftovers()
    {
        if (!Directory.Exists(_folder)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(_folder, ".*"))
        {
            if (Directory.Exists(entry)) TryDeleteDirectory(entry);
            else TryDelete(entry);
        }
    }

    private RcloneInstallState Load()
    {
        var path = Path.Combine(_folder, StateFile);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(path), RcloneInstallJsonContext.Default.RcloneInstallState) ?? new RcloneInstallState()
                : new RcloneInstallState();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _log($"rclone's install state could not be read ({e.GetType().Name}); it starts again.");
            return new RcloneInstallState();
        }
    }

    private async Task SaveAsync(RcloneInstallState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);
        await AtomicFile.WriteBytesAsync(Path.Combine(_folder, StateFile),
            JsonSerializer.SerializeToUtf8Bytes(state, RcloneInstallJsonContext.Default.RcloneInstallState), cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _state, state);
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log($"rclone: {Path.GetFileName(path)} could not be removed ({e.GetType().Name})."); }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"rclone: {Path.GetFileName(path)} could not be removed ({e.GetType().Name}).");
        }
    }
}
