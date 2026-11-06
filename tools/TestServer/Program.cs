using TestServer;

var port = 0;
var withProxy = false;
var withSocks5 = false;
var withTorBox = false;
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

    if (args[i] == "--socks5")
    {
        withSocks5 = true;
    }

    if (args[i] == "--torbox")
    {
        withTorBox = true;
    }
}

var host = await TestServerHost.StartAsync(port);
TestProxyServer? proxy = null;
TestSocks5Server? socks5 = null;
Console.WriteLine($"Test server listening at {host.BaseAddress}");
Console.WriteLine($"FTP listening at 127.0.0.1:{host.Ftp.Port} (ftpuser/ftppass)");
if (withTorBox)
{
    host.TorBox.SeedSandbox();
    // Invented, like everything in the mock. Point a sandbox Host at it with DOWNLOADMANAGER_TORBOX_API.
    Console.WriteLine($"Mock TorBox API at {host.TorBox.ApiBase} (key {MockTorBox.DefaultApiKey})");
}

if (withProxy)
{
    proxy = TestProxyServer.Start();
    Console.WriteLine($"Authenticated proxy listening at {proxy.Address} (proxyuser/proxypass)");
}

if (withSocks5)
{
    socks5 = TestSocks5Server.Start();
    Console.WriteLine($"Authenticated SOCKS5 listening at {socks5.Address} (socksuser/sockspass)");
}

Console.WriteLine("Press Ctrl+C to stop.");
var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
if (proxy is not null || socks5 is not null)
{
    _ = Task.Run(async () =>
    {
        while (!stop.Task.IsCompleted)
        {
            await Task.Delay(2000);
            if (proxy is not null)
            {
                Console.WriteLine(
                    $"proxy authorized={proxy.Authorized} denied={proxy.Denied} " +
                    $"tunnels={proxy.Tunnels} last={proxy.LastTarget}");
            }

            if (socks5 is not null)
            {
                Console.WriteLine(
                    $"socks5 authorized={socks5.Authorized} denied={socks5.Denied} " +
                    $"connects={socks5.Connects} last={socks5.LastTarget}");
            }
        }
    });
}

await stop.Task;
if (socks5 is not null)
{
    await socks5.DisposeAsync();
}

if (proxy is not null)
{
    await proxy.DisposeAsync();
}

await host.DisposeAsync();
