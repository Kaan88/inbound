using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Providers.TorBox;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Host;

/// <summary>
/// The one place that names a concrete provider. Everything else finds providers through the
/// registry, so adding one means an implementation and a line here.
/// </summary>
internal sealed record RemoteProviderSetup(RemoteProviderRegistry Registry, RemoteLinkBroker Broker, HttpClient Http) : IDisposable
{
    /// <summary>A sandbox profile can point at the mock API. Rules in <see cref="TorBoxApiAddress"/>, because the key goes wherever this says.</summary>
    private const string SandboxApiVariable = "DOWNLOADMANAGER_TORBOX_API";

    public static RemoteProviderSetup Create(StoragePaths paths, HttpDownloadEngine engine, Action<string> log)
    {
        var http = new HttpClient(new ProxiedClientHandler(engine)) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DownloadManager/0.1");

        var registry = new RemoteProviderRegistry();
        // Opening never throws for a key it can't read; that becomes the account's state.
        var account = RemoteAccountStore.OpenAsync(paths, TorBoxClient.ProviderId, log).GetAwaiter().GetResult();
        registry.Add(new TorBoxClient(http, account.KeySource, SandboxApi(log)), account);
        return new RemoteProviderSetup(registry, new RemoteLinkBroker(registry, log: log), http);
    }

    public RemoteJobCoordinator CreateCoordinator(StoragePaths paths, IRemoteJobHost host, Action<string> log)
        => new(Registry, new JsonRemoteJobStore(paths, log), host, log: log);

    public void Dispose() => Http.Dispose();

    private static Uri? SandboxApi(Action<string> log)
    {
        // The same two settings that give this process a state folder and a pipe of its own.
        var sandboxProfile =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTANCE")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA"));
        var uri = TorBoxApiAddress.FromOverride(Environment.GetEnvironmentVariable(SandboxApiVariable), sandboxProfile, out var problem);
        if (problem is not null)
        {
            log($"{SandboxApiVariable} was ignored: {problem}.");
        }
        else if (uri is not null)
        {
            log("Using another TorBox API address for this sandbox profile.");
        }

        return uri;
    }
}
