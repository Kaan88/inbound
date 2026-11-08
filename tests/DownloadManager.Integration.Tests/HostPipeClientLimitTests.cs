using DownloadManager.Contracts;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Each progress or completion window is a window process with its own connection to the Host. With room for
/// only eight, the main window and seven open progress windows left the browser bridge and the CLI with no way
/// in (found in the PERF.2 soak: "All pipe instances are busy"). The real Host must take many more.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class HostPipeClientLimitTests : IAsyncLifetime
{
    private TestServerHost _server = null!;

    public async Task InitializeAsync() => _server = await TestServerHost.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task TheHostTakesMoreThanEightClientsAtOnce()
    {
        await using var host = await SandboxHost.StartAsync(_server.TorBox.ApiBase);
        var connections = new List<HostConnection>();
        try
        {
            // One at a time, as windows open: each must get its hello and snapshot within OpenAsync's 10 s.
            for (var i = 0; i < 16; i++)
            {
                connections.Add(await host.ConnectAsync());
            }

            foreach (var connection in connections)
            {
                var reply = await connection.RequestAsync(MessageTypes.HostPing, new { });
                Assert.Equal("pong", reply.GetPayload<HostActionResult>()!.Message);
            }
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }
}
