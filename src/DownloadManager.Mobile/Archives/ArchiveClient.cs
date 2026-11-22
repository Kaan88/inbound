using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Archives;

/// <summary>
/// <c>archive.*</c> requests on the selected, already-open Host connection (the phone's own Host or a paired one). The Host extracts;
/// this only sends the messages and reads the replies. A password goes out in <see cref="SetPasswordAsync"/> and is never kept, logged or
/// read back (no payload carries one back).
/// </summary>
public sealed class ArchiveClient
{
    /// <summary>What the hello lists when the Host answers <c>archive.*</c> and an extractor (7-Zip) is there.</summary>
    public const string Capability = "archives";

    public const string HostRefused = "The Host did not accept the request.";

    private readonly IHostConnection _connection;

    public ArchiveClient(IHostConnection connection) =>
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public static bool Offered(HostHelloDto? hello) => hello?.Capabilities.Contains(Capability) == true;

    /// <summary>
    /// The Host keeps saved passwords and a set's own delete choice. An older Host ignores <c>saveForOtherArchives</c> and
    /// <c>deleteArchives</c> and doesn't send the set's choice, so the phone hides those controls there (Fable F1).
    /// </summary>
    public const string SavedPasswordsCapability = "archiveSavedPasswords";

    public static bool SavedPasswordsOffered(HostHelloDto? hello) => hello?.Capabilities.Contains(SavedPasswordsCapability) == true;

    public Task<ArchiveListResult> ListAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<ArchiveListResult>(IpcMessage.Create(ArchiveMessageTypes.List, new EmptyPayload()), ArchiveMessageTypes.ListResult, cancellationToken);

    public Task<ArchiveSettingsResult> SettingsAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<ArchiveSettingsResult>(IpcMessage.Create(ArchiveMessageTypes.SettingsGet, new EmptyPayload()), ArchiveMessageTypes.SettingsResult, cancellationToken);

    public Task<ArchiveSettingsResult> UpdateSettingsAsync(ArchiveSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return RequestAsync<ArchiveSettingsResult>(IpcMessage.Create(ArchiveMessageTypes.SettingsSet, update), ArchiveMessageTypes.SettingsResult, cancellationToken);
    }

    /// <summary>Gives the set its password and lets the Host try again.</summary>
    /// <param name="saveForOtherArchives">Also adds it to the Host's saved passwords, tried on any archive that needs one.</param>
    /// <param name="deleteArchives">The set's own choice to delete its archives once extracted; null leaves it as it is.</param>
    public Task<ArchiveActionResult> SetPasswordAsync(Guid setId, string password, bool saveForOtherArchives = false, bool? deleteArchives = null,
        CancellationToken cancellationToken = default) =>
        RequestAsync<ArchiveActionResult>(IpcMessage.Create(ArchiveMessageTypes.SetPassword, PasswordRequest(setId, password, saveForOtherArchives, deleteArchives)),
            ArchiveMessageTypes.ActionResult, cancellationToken);

    /// <summary>The Host's saved passwords: ids and dates only, in the order they are tried.</summary>
    public Task<ArchiveSavedPasswordsResult> SavedPasswordsAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<ArchiveSavedPasswordsResult>(IpcMessage.Create(ArchiveMessageTypes.PasswordsList, new EmptyPayload()), ArchiveMessageTypes.PasswordsResult, cancellationToken);

    public Task<ArchiveSavedPasswordsResult> AddSavedPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return RequestAsync<ArchiveSavedPasswordsResult>(IpcMessage.Create(ArchiveMessageTypes.PasswordsAdd, new ArchiveSavedPasswordAddRequest { Password = password }),
            ArchiveMessageTypes.PasswordsResult, cancellationToken);
    }

    public Task<ArchiveSavedPasswordsResult> RemoveSavedPasswordAsync(Guid id, CancellationToken cancellationToken = default) =>
        RequestAsync<ArchiveSavedPasswordsResult>(IpcMessage.Create(ArchiveMessageTypes.PasswordsRemove, new ArchiveSavedPasswordRemoveRequest { Id = id }),
            ArchiveMessageTypes.PasswordsResult, cancellationToken);

    public Task<ArchiveActionResult> RetryAsync(Guid setId, CancellationToken cancellationToken = default) =>
        RequestAsync<ArchiveActionResult>(IpcMessage.Create(ArchiveMessageTypes.Retry, new ArchiveActionRequest { SetId = setId }), ArchiveMessageTypes.ActionResult, cancellationToken);

    /// <summary>The request for one set. Only the set's own id: no path, so nothing else on the Host gets this password.</summary>
    public static ArchiveSetPasswordRequest PasswordRequest(Guid setId, string password, bool saveForOtherArchives = false, bool? deleteArchives = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return new ArchiveSetPasswordRequest { SetId = setId, Password = password, SaveForOtherArchives = saveForOtherArchives, DeleteArchives = deleteArchives };
    }

    /// <summary>"3, tried in order" under Saved passwords.</summary>
    public static string SavedPasswordsLine(int count) => count switch
    {
        0 => "None yet",
        1 => "1, tried before asking",
        _ => $"{count}, tried in order",
    };

    private async Task<T> RequestAsync<T>(IpcMessage request, string expectedType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = await _connection.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Type == MessageTypes.HostActionResult)
            throw new InvalidOperationException(reply.GetPayload<HostActionResult>().Message ?? HostRefused);
        // A Host that refuses a request it could not read answers with the action result whatever the request was.
        if (reply.Type == ArchiveMessageTypes.ActionResult && expectedType != ArchiveMessageTypes.ActionResult)
            throw new InvalidOperationException(reply.GetPayload<ArchiveActionResult>().Error ?? HostRefused);
        if (reply.Type != expectedType)
            throw new InvalidDataException($"The Host answered with '{reply.Type}' instead of '{expectedType}'.");
        return reply.GetPayload<T>();
    }
}
