using System.Text;

namespace DownloadManager.Host;

public sealed class FileLogger
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _prefix;
    private string? _currentDate;
    private string? _currentPath;

    public FileLogger(string directory, string prefix = "host")
    {
        _directory = directory;
        _prefix = prefix;
        Directory.CreateDirectory(_directory);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                var now = DateTimeOffset.Now;
                var date = now.ToString("yyyyMMdd");
                if (_currentDate != date || _currentPath is null)
                {
                    _currentDate = date;
                    _currentPath = Path.Combine(_directory, $"{_prefix}-{date}.log");
                }

                var line = $"{now:O} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_currentPath, line, new UTF8Encoding(false));
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
