namespace DownloadManager.Server;

/// <summary>A configuration the server refuses to start with. The message says what to change.</summary>
public sealed class ServerStartException(string message) : Exception(message);
