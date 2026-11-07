using System.Runtime.InteropServices;
using System.Windows.Forms;
using DownloadManager.Core;

namespace DownloadManager.Host;

/// <summary>
/// Polls the Windows clipboard on the Host UI thread and raises HTTP(S) URLs.
/// Clipboard access requires STA, which the Host main thread provides.
/// </summary>
internal sealed class HostClipboardMonitor : IDisposable
{
    private readonly Func<bool> _enabled;
    private readonly Action<string> _onUrl;
    private readonly Func<string, string> _describe;
    private readonly Action<string> _log;
    private readonly System.Windows.Forms.Timer _timer;
    private string? _lastText;
    private string? _lastUrl;
    private DateTimeOffset _lastUrlAt;
    private bool _disposed;

    /// <param name="describe">What the log may say about a copied address.</param>
    public HostClipboardMonitor(Func<bool> enabled, Action<string> onUrl, Func<string, string> describe, Action<string> log)
    {
        _enabled = enabled;
        _onUrl = onUrl;
        _describe = describe;
        _log = log;
        _timer = new System.Windows.Forms.Timer { Interval = 600 };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Runs the poll only while monitoring is on, so the Host doesn't wake for it otherwise. Call on the UI thread.</summary>
    public void Start() => Apply();

    /// <summary>Starts or stops polling to match the setting. Call on the UI thread.</summary>
    public void Apply()
    {
        if (_disposed) return;
        if (_enabled()) _timer.Start();
        else _timer.Stop();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }

    private void Poll()
    {
        try
        {
            if (!_enabled())
            {
                return;
            }

            if (!Clipboard.ContainsText())
            {
                return;
            }

            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, _lastText, StringComparison.Ordinal))
            {
                return;
            }

            _lastText = text;
            var url = ClipboardUrlExtractor.FirstHttpUrl(text);
            if (url is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (string.Equals(url, _lastUrl, StringComparison.OrdinalIgnoreCase) &&
                now - _lastUrlAt < TimeSpan.FromMinutes(1))
            {
                return;
            }

            _lastUrl = url;
            _lastUrlAt = now;
            _log($"Clipboard URL detected: {_describe(url)}");
            _onUrl(url);
        }
        catch (ExternalException ex)
        {
            // Another process owns the clipboard; retry on the next tick.
            _log($"Clipboard read skipped: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log($"Clipboard monitor error: {ex.Message}");
        }
    }
}
