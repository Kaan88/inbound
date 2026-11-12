using System.Diagnostics;
using DownloadManager.Core;

namespace DownloadManager.Host;

internal sealed class HostCompletionActionExecutor : ICompletionActionExecutor
{
    private readonly Action<string> _log;

    public HostCompletionActionExecutor(Action<string> log)
    {
        _log = log;
    }

    public Action? ExitHost { get; set; }

    public Task OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task RequestExitAsync(CancellationToken cancellationToken)
    {
        var exit = ExitHost ?? throw new InvalidOperationException("Host exit handler is not ready.");
        _log("Completion action: exiting the host.");
        exit();
        return Task.CompletedTask;
    }

    public Task RequestShutdownAsync(string mode, bool force, CancellationToken cancellationToken)
    {
        var arguments = ShutdownCommand.BuildArguments(mode, force);
        _log($"Completion action: running shutdown.exe {arguments}.");
        // By its full path: a bare name is looked for in the Host's own and current folders first (report 3, L3).
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return Task.CompletedTask;
    }
}
