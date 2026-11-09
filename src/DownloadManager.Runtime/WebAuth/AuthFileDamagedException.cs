namespace DownloadManager.Runtime.WebAuth;

/// <summary>
/// <c>auth.json</c> can't be read (<see cref="AuthStore.Open"/>). Nothing in it is used: a damaged file would otherwise
/// read as "no password yet". The server and the web bridge refuse to start with it; the desktop window replaces it
/// when a new password is set there.
/// </summary>
public sealed class AuthFileDamagedException(string message) : Exception(message);
