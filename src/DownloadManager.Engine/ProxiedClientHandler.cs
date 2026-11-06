namespace DownloadManager.Core;

/// <summary>
/// For other HTTP clients in the app, such as a provider's API client: their traffic takes the same
/// route as downloads, and follows the engine when the proxy settings change. An open connection
/// keeps the proxy it was opened through, so each change gets a new inner handler. Requests already
/// running stay on the old one and finish there, and whoever holds this handler keeps its own state.
/// </summary>
public sealed class ProxiedClientHandler : HttpMessageHandler
{
    private readonly HttpDownloadEngine _engine;
    private readonly Lock _sync = new();
    private readonly List<HttpMessageInvoker> _retired = [];
    private HttpMessageInvoker _current;
    private bool _disposed;

    public ProxiedClientHandler(HttpDownloadEngine engine)
    {
        _engine = engine;
        _current = Create();
        _engine.ProxyChanged += OnProxyChanged;
    }

    private HttpMessageInvoker Create() => new(_engine.CreateProxiedHandler(), disposeHandler: true);

    private void OnProxyChanged()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            // Disposing now would cut off whatever is still running. This happens only when the
            // user changes the proxy, so keeping the old ones until shutdown costs next to nothing.
            _retired.Add(_current);
            _current = Create();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpMessageInvoker current;
        lock (_sync) current = _current;
        return current.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _engine.ProxyChanged -= OnProxyChanged;
                    _current.Dispose();
                    _retired.ForEach(invoker => invoker.Dispose());
                    _retired.Clear();
                }
            }
        }

        base.Dispose(disposing);
    }
}
