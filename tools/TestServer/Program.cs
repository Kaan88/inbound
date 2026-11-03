using TestServer;

var host = await TestServerHost.StartAsync();
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
