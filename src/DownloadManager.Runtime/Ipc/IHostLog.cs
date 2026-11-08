namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// Where the command handling writes what happened. The desktop Host's <c>FileLogger</c> is one; a server writes
/// to its own log. Lines can come from any thread at once.
/// </summary>
public interface IHostLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}
