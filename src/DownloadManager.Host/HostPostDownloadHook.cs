using System.Diagnostics;
using DownloadManager.Core;

namespace DownloadManager.Host;

internal sealed class HostPostDownloadHook : IPostDownloadHook
{
    private readonly Action<string> _log;

    public HostPostDownloadHook(Action<string> log)
    {
        _log = log;
    }

    public async Task RunAsync(string command, string filePath, CancellationToken cancellationToken)
    {
        var start = PostDownloadCommandLine.Create(command, filePath);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The post-download program could not be started.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            throw new TimeoutException("The post-download program timed out.");
        }

        var output = (await stdout.ConfigureAwait(false)).Trim();
        var error = (await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The post-download program exited with {process.ExitCode}" +
                (error.Length == 0 ? "." : $": {error}"));
        }

        if (output.Length > 0)
        {
            _log($"Post-download program output: {output}");
        }
    }
}
