using TestServer;

var port = 0;
var withProxy = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--port" or "-p" && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed))
    {
        port = parsed;
    }

    if (args[i] == "--proxy")
    {
        withProxy = true;
    }
}

var host = await TestServerHost.StartAsync(port);
TestProxyServer? proxy = null;
Console.WriteLine($"Test server listening at {host.BaseAddress}");
if (withProxy)
{
    proxy = TestProxyServer.Start();
    Console.WriteLine($"Authenticated proxy listening at {proxy.Address} (proxyuser/proxypass)");
}

Console.WriteLine("Press Ctrl+C to stop.");
var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
if (proxy is not null)
{
    _ = Task.Run(async () =>
    {
        while (!stop.Task.IsCompleted)
        {
            await Task.Delay(2000);
            Console.WriteLine(
                $"proxy authorized={proxy.Authorized} denied={proxy.Denied} " +
                $"tunnels={proxy.Tunnels} last={proxy.LastTarget}");
        }
    });
}

await stop.Task;
if (proxy is not null)
{
    await proxy.DisposeAsync();
}

await host.DisposeAsync();
