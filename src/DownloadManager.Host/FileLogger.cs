using System.Text;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Host;

public sealed class FileLogger : IHostLog
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _prefix;
    private string? _currentDate;
    private string? _currentPath;
    private long _currentSize;
    private bool _capped;

    /// <summary>L7: deletes this prefix's files older than <see cref="LogFileRetention.KeepDays"/> days, at start and when the date changes.</summary>
    public FileLogger(string directory, string prefix = "host")
    {
        _directory = directory;
        _prefix = prefix;
        Directory.CreateDirectory(_directory);
        LogFileRetention.DeleteOld(_directory, _prefix, DateTime.Now);
    }

    /// <summary>Account keys to keep out of the file, wherever a line happens to quote one.</summary>
    public IRemoteSecretMatcher? Secrets { get; set; }

    /// <summary>One day's file stops growing here (L7); tests set a smaller limit.</summary>
    public long MaxDailyBytes { get; init; } = LogFileRetention.MaxDailyBytes;

    /// <summary>The time lines are stamped with; tests move it to another day.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.Now;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                var now = Clock();
                var date = now.ToString("yyyyMMdd");
                if (_currentDate != date || _currentPath is null)
                {
                    // L7: a Host that runs for weeks prunes once a day, not only at start.
                    if (_currentDate is not null) LogFileRetention.DeleteOld(_directory, _prefix, now.DateTime);
                    _currentDate = date;
                    _currentPath = Path.Combine(_directory, $"{_prefix}-{date}.log");
                    _currentSize = LogFileRetention.CurrentSize(_currentPath);
                    _capped = _currentSize >= MaxDailyBytes;
                }

                if (_capped) return;

                // Every line, so an address in an unexpected exception's text can't reach the file.
                var text = UrlRedactor.RedactText(Secrets?.Mask(message) ?? message);
                var line = $"{now:O} [{level}] {text}{Environment.NewLine}";
                var size = Utf8.GetByteCount(line);
                if (_currentSize + size > MaxDailyBytes)
                {
                    _capped = true;
                    line = $"{now:O} [WARN] {LogFileRetention.CapReachedLine}{Environment.NewLine}";
                    size = Utf8.GetByteCount(line);
                }

                File.AppendAllText(_currentPath, line, Utf8);
                _currentSize += size;
            }
        }
        catch (IOException)
        {
            // Logging must never take down the host.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging must never take down the host.
        }
    }
}
