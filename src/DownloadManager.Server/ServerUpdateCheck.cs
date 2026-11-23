using System.Net;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime;

namespace DownloadManager.Server;

/// <summary>
/// UPD.D1: the headless server's own update check. It reads a small signed file, <c>manifest-docker.json</c>, that the
/// Docker workflow attaches to each release it builds (a Windows-only release carries the previous one, as the Linux file
/// is carried), so Docker installs are only told about images that exist. It checks once a day and at start, is turned off
/// by <c>INBOUND_UPDATE_CHECK=false</c>, and never updates itself: it only records the newest version and its changelog
/// lines, which the web UI and a paired desktop app show as the notice. The desktop Host (and its web bridge) has its own
/// check and never runs this.
/// </summary>
public sealed class ServerUpdateCheck : IDisposable
{
    public const string DisableVariable = "INBOUND_UPDATE_CHECK";
    public const string ManifestName = "manifest-docker.json";
    public const string ManifestUrl = "https://github.com/Kaan88/inbound/releases/latest/download/" + ManifestName;

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private const int MaxManifestBytes = 1024 * 1024;

    private readonly DownloadManagerService _service;
    private readonly Action<string> _log;
    private readonly string _publicKey;
    private readonly string _installedVersion;
    private readonly string _manifestUrl;
    private readonly HttpClient _client;
    private readonly Timer _timer;
    private readonly object _sync = new();
    private bool _checking;
    private bool _disposed;
    private volatile bool _enabled = true;

    /// <param name="handler">For tests; the client is disposed with this check only when a handler is not given.</param>
    public ServerUpdateCheck(DownloadManagerService service, Action<string> log, string publicKey, string installedVersion,
        string? manifestUrl = null, HttpMessageHandler? handler = null)
    {
        _service = service;
        _log = log;
        _publicKey = publicKey;
        _installedVersion = installedVersion;
        _manifestUrl = manifestUrl ?? ManifestUrl;
        _client = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _timer = new Timer(_ => _ = CheckAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>False when <c>INBOUND_UPDATE_CHECK=false</c>: nothing is checked and nothing is recorded.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Arms the daily check and checks at once, unless it is turned off.</summary>
    public void Start()
    {
        if (!Enabled) return;
        _timer.Change(Interval, Interval);
        _ = CheckAsync();
    }

    /// <summary>The one check: fetch and verify the Docker manifest, then record its version and notes. Never installs.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_checking) return;
            _checking = true;
        }

        try
        {
            string? version = null;
            List<ChangelogItem>? notes = null;
            try
            {
                using var response = await _client.GetAsync(_manifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _log($"Docker update check: the manifest answered HTTP {(int)response.StatusCode}.");
                }
                else
                {
                    var json = response.Content.Headers.ContentLength is > MaxManifestBytes
                        ? null
                        : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (json is null || json.Length > MaxManifestBytes)
                    {
                        _log("Docker update check: the manifest is too large.");
                    }
                    else
                    {
                        var manifest = UpdateManifestVerifier.Parse(json);
                        if (manifest is not null && UpdateManifestVerifier.Verify(manifest, _publicKey))
                        {
                            version = manifest.Version;
                            notes = UpdateManifestVerifier.VerifiedNotes(manifest, _publicKey);
                        }
                        else
                        {
                            _log("Docker update check: the manifest signature is invalid.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _log($"Docker update check failed: {ex.Message}");
            }

            // A failed or unverified check says nothing new: the version found before stays known.
            if (version is not null) _service.NoteServerUpdate(version, notes);
        }
        finally
        {
            lock (_sync) _checking = false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _timer.Dispose();
        _client.Dispose();
    }
}
