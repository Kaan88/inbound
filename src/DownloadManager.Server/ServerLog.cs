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
    private string? _currentDate;
    private string? _currentPath;

    public ServerLog(string directory, bool console = true)
    {
        _directory = directory;
        _console = console;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Account keys to keep out of the log, wherever a line happens to quote one.</summary>
    public IRemoteSecretMatcher? Secrets { get; set; }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var text = UrlRedactor.RedactText(Secrets?.Mask(message) ?? message);
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
                    _currentPath = Path.Combine(_directory, $"server-{date}.log");
                }

                File.AppendAllText(_currentPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take down the server.
            }
        }
    }
}
