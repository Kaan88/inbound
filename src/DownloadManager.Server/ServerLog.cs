using System.Text;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Server;

/// <summary>
/// The server's log: one line per message to standard output, which is what <c>docker logs</c> shows, and to a daily
/// file in the data folder's <c>logs</c>. Every line goes through the same redaction as the desktop Host's log: provider
/// keys are masked and addresses lose their query and credentials, so neither reaches the container's log either.
/// </summary>
public sealed class ServerLog : IHostLog
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly bool _console;
    private readonly string _fileName;
    private string? _currentDate;
    private string? _currentPath;
    private long _currentSize;
    private bool _capped;

    /// <param name="fileName">The daily file's name before the date: <c>server</c>, or <c>web-bridge</c> in the desktop Host's logs.</param>
    public ServerLog(string directory, bool console = true, string fileName = "server")
    {
        _directory = directory;
        _console = console;
        _fileName = fileName;
        Directory.CreateDirectory(_directory);
        // L7: files older than 30 days go at start; a day's file stops at a size limit (see Write).
        DownloadManager.Persistence.LogFileRetention.DeleteOld(_directory, _fileName, DateTime.Now);
    }

    /// <summary>One day's file stops growing here (L7); standard output still gets every line.</summary>
    public long MaxDailyBytes { get; init; } = DownloadManager.Persistence.LogFileRetention.MaxDailyBytes;

    /// <summary>Account keys to keep out of the log, wherever a line happens to quote one.</summary>
    public IRemoteSecretMatcher? Secrets { get; set; }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var text = OneLine(UrlRedactor.RedactText(Secrets?.Mask(message) ?? message));
        var now = DateTimeOffset.Now;
        var line = $"{now:O} [{level}] {text}";
        lock (_gate)
        {
            if (_console)
            {
                try
                {
                    Console.Out.WriteLine(line);
                }
                catch (IOException)
                {
                    // A closed stdout must not take the server down.
                }
            }

            try
            {
                var date = now.ToString("yyyyMMdd");
                if (_currentDate != date || _currentPath is null)
                {
                    _currentDate = date;
                    _currentPath = Path.Combine(_directory, $"{_fileName}-{date}.log");
                    _currentSize = DownloadManager.Persistence.LogFileRetention.CurrentSize(_currentPath);
                    _capped = _currentSize >= MaxDailyBytes;
                }

                if (_capped) return;
                var encoding = new UTF8Encoding(false);
                var fileLine = line + Environment.NewLine;
                var size = encoding.GetByteCount(fileLine);
                if (_currentSize + size > MaxDailyBytes)
                {
                    _capped = true;
                    fileLine = $"{now:O} [WARN] {DownloadManager.Persistence.LogFileRetention.CapReachedLine}{Environment.NewLine}";
                    size = encoding.GetByteCount(fileLine);
                }

                File.AppendAllText(_currentPath, fileLine, encoding);
                _currentSize += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take down the server.
            }
        }
    }

    /// <summary>
    /// One message is one line: line breaks and other control characters are written as escapes (<c>\n</c>,
    /// <c>\u001b</c>), so text a client chose (a message type, a name) can't add a line that looks like the server's
    /// own, such as a sign-in that never happened. Tabs stay.
    /// </summary>
    internal static string OneLine(string text)
    {
        if (!text.Any(c => char.IsControl(c) && c != '\t')) return text;
        var line = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (c == '\t' || !char.IsControl(c)) line.Append(c);
            else if (c == '\n') line.Append("\\n");
            else if (c == '\r') line.Append("\\r");
            else line.Append("\\u").Append(((int)c).ToString("x4"));
        }

        return line.ToString();
    }
}
