using DownloadManager.Server;
using DownloadManager.Server.Bridge;

// The desktop Host's web bridge (D8): its own exit codes (WebBridgeExit), options on standard input.
if (args.Contains("--bridge", StringComparer.Ordinal))
    return await BridgeApp.RunAsync(Console.In, Console.Out, Console.Error);

// Exit codes: 0 stopped normally, 2 a configuration the server refuses (the message says what to change), 1 a failure.
var options = ServerOptions.FromEnvironment();
ServerApp app;
try
{
    app = await ServerApp.StartAsync(options);
}
catch (ServerStartException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{
    // A data folder that can't be used, or a damaged key file. Nothing can be logged to the folder in the first case.
    Console.Error.WriteLine($"The server could not start: {ex.Message}");
    return 1;
}

await using (app)
{
    await app.WaitForShutdownAsync();
}

return 0;
