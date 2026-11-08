using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;

// One secret protector per process (SecretProtector.Use), and each server sets its own: one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DownloadManager.Server.Tests;

/// <summary>A real server on a random loopback port, with a data folder and one root of its own.</summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    private readonly bool _ownsWork;

    private ServerHarness(string work, ServerApp app, bool ownsWork)
    {
        _ownsWork = ownsWork;
        Work = work;
        App = app;
        Base = app.Addresses.First(address => address.Scheme == "http");
        Http = new HttpClient { BaseAddress = Base, Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Work { get; }
    public string Data => Path.Combine(Work, "data");
    public string Root => Path.Combine(Work, "downloads");
    public ServerApp App { get; }
    public Uri Base { get; }
    public HttpClient Http { get; }

    public static string NewWork()
    {
        var work = Path.Combine(AppContext.BaseDirectory, "test-work", "server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "downloads"));
        return work;
    }

    /// <summary>A <paramref name="work"/> folder given by the test is kept, so a second server can start on the same data.</summary>
    public static async Task<ServerHarness> StartAsync(Func<ServerOptions, ServerOptions>? change = null, string? work = null)
    {
        var ownsWork = work is null;
        work ??= NewWork();
        Directory.CreateDirectory(Path.Combine(work, "downloads"));
        var options = new ServerOptions
        {
            DataDirectory = Path.Combine(work, "data"),
            Roots = [Path.Combine(work, "downloads")],
            Urls = ["http://127.0.0.1:0"],
        };
        if (change is not null) options = change(options);
        var app = await ServerApp.StartAsync(options, logToConsole: false);
        return new ServerHarness(work, app, ownsWork);
    }

    public Uri WebSocketAddress => new UriBuilder(Base) { Scheme = "ws", Path = "/api/ws" }.Uri;

    public async Task<WsClient> ConnectAsync(string? origin = null)
    {
        var socket = new ClientWebSocket();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(WebSocketAddress, timeout.Token);
        return new WsClient(socket);
    }

    /// <summary>One envelope to <c>/api/ipc</c>; the reply's payload.</summary>
    public async Task<T> AskAsync<T>(string type, object payload)
    {
        var request = IpcMessage.Create(type, payload);
        using var response = await Http.PostAsync("/api/ipc", JsonContent.Create(request, options: IpcJson.Options));
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<IpcMessage>(IpcJson.Options);
        Assert.NotNull(reply);
        Assert.Equal(request.Id, reply!.ReplyTo);
        return reply.GetPayload<T>();
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.DisposeAsync();
        if (!_ownsWork) return;
        try
        {
            Directory.Delete(Work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file a background step still holds; the next build's clean removes test-work.
        }
    }
}

/// <summary>A WebSocket client of the server: every envelope it was sent is kept, in order.</summary>
internal sealed class WsClient(ClientWebSocket socket) : IAsyncDisposable
{
    private readonly List<IpcMessage> _received = [];

    public ClientWebSocket Socket => socket;

    public IReadOnlyList<IpcMessage> Received => _received;

    public async Task SendAsync(IpcMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        await socket.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>The next envelope, or null when the server closed.</summary>
    public async Task<IpcMessage?> ReadAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        var envelope = JsonSerializer.Deserialize<IpcMessage>(message.ToArray(), IpcJson.Options)!;
        _received.Add(envelope);
        return envelope;
    }

    /// <summary>Reads until <paramref name="match"/> holds for an envelope, and returns that one.</summary>
    public async Task<IpcMessage> ReadUntilAsync(Func<IpcMessage, bool> match, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException("The expected envelope did not come. Got: " + string.Join(", ", _received.Select(m => m.Type)));
            var next = await ReadAsync(left) ?? throw new InvalidOperationException("The server closed the connection.");
            if (match(next)) return next;
        }
    }

    public async Task<T> AskAsync<T>(string type, object payload)
    {
        var request = IpcMessage.Create(type, payload);
        await SendAsync(request);
        return (await ReadUntilAsync(message => message.ReplyTo == request.Id)).GetPayload<T>();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // already gone
        }

        socket.Dispose();
    }
}

internal static class Text
{
    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
