using TestServer;

var port = 0;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var parsed))
    {
        port = parsed;
    }
}

var host = await TestServerHost.StartAsync(port);
Console.WriteLine($"Test server listening at {host.BaseAddress}");
Console.WriteLine("Press Ctrl+C to stop.");
var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
await host.DisposeAsync();
