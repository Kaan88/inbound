using DownloadManager.Runtime.WebUi;
using DownloadManager.Server;
using DownloadManager.Server.Auth;

namespace DownloadManager.Host;

/// <summary>
/// The web UI's password in the bridge's own sign-in file (<c>auth.json</c> in the data folder; the server's
/// <see cref="AuthStore"/> and <see cref="PasswordHash"/>, compiled into the Host from the server's sources, no ASP.NET).
/// Only its PBKDF2 hash is kept. Set while the bridge is stopped. Setting it here is a change made outside the web UI,
/// as the server's environment password is, so it ends what the old one opened: the saved sessions go
/// (<c>sessions.json</c>, which the bridge would also drop by the password's stamp) and every paired app is unpaired.
/// </summary>
public sealed class WebUiPasswordFile(string dataDirectory, Action<string> warn) : IWebUiPasswords
{
    /// <summary>The bridge's saved sessions (the server's <c>SessionStore.FileName</c>).</summary>
    public const string SessionsFileName = "sessions.json";

    public bool HasPassword()
    {
        if (!File.Exists(Path.Combine(dataDirectory, AuthStore.FileName))) return false;
        try
        {
            return AuthStore.Open(dataDirectory).Password is not null;
        }
        catch (ServerStartException)
        {
            // Damaged: the bridge refuses it too. Setting a password here starts it again.
            return false;
        }
    }

    public string? Problem(string? password) => PasswordHash.Problem(password);

    public async Task SetAsync(string password, CancellationToken cancellationToken)
    {
        if (PasswordHash.Problem(password) is { } problem) throw new ArgumentException(problem, nameof(password));
        var record = await PasswordHash.CreateAsync(password, DateTimeOffset.UtcNow, "app", cancellationToken).ConfigureAwait(false);
        AuthStore store;
        try
        {
            store = AuthStore.Open(dataDirectory);
        }
        catch (ServerStartException)
        {
            // A damaged file holds nothing usable; the user is choosing a new password now.
            var path = Path.Combine(dataDirectory, AuthStore.FileName);
            warn("The web UI's damaged sign-in file was replaced with the new password.");
            File.Move(path, path + ".damaged", overwrite: true);
            store = AuthStore.Open(dataDirectory);
        }

        // The tokens first, so a failed save can't leave the new password with the old pairings.
        store.RemoveAllTokens();
        store.SetPassword(record);
        var sessions = Path.Combine(dataDirectory, SessionsFileName);
        try
        {
            File.Delete(sessions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The bridge drops them anyway: they carry the old password's stamp.
            warn($"The web UI's saved sessions could not be removed: {ex.GetType().Name}");
        }
    }
}
