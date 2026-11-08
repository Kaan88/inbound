namespace DownloadManager.Server;

/// <summary>
/// One server per data folder, for the whole life of the process. The desktop Host gets this from its named mutex; a
/// server has none to share (two containers on one volume, a development run next to a container, a restart that
/// overlaps the old process), so it holds <c>server.lock</c> in the data folder open with no sharing. .NET turns that
/// into an exclusive <c>flock</c> on Linux, which the kernel drops when the process ends, killed or not, so a stale file
/// never blocks a start. Two writers would both run downloads and rewrite the same job and checkpoint files.
/// A file system without working locks (some network shares) can't give this guarantee: keep the data on a local volume.
/// </summary>
public sealed class DataFolderLock : IDisposable
{
    public const string FileName = "server.lock";

    private readonly FileStream _stream;

    private DataFolderLock(FileStream stream) => _stream = stream;

    /// <exception cref="ServerStartException">Another process holds the folder.</exception>
    public static DataFolderLock Acquire(string dataFolder)
    {
        var path = Path.Combine(dataFolder, FileName);
        FileStream stream;
        try
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
            });
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException)
        {
            throw new ServerStartException(
                $"Another server is already using the data folder {dataFolder}. Stop it first, or give this one its own folder ({ServerOptions.DataVariable}).");
        }

        try
        {
            // Only for a person looking at the folder: which process has it. Nothing reads it back.
            stream.SetLength(0);
            var note = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stream.Write(note);
            stream.Flush();
        }
        catch (IOException)
        {
            // The lock is what matters; the note is not.
        }

        return new DataFolderLock(stream);
    }

    public void Dispose() => _stream.Dispose();
}
