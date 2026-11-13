using System.Text.Json;
using System.Text.Json.Nodes;

namespace DownloadManager.Contracts;

/// <summary>Who is on the far side of a bridge into the Host, which decides what may cross it.</summary>
public enum BridgeRole
{
    /// <summary>The browser extension, through the native-messaging host.</summary>
    BrowserExtension,

    /// <summary>
    /// The browser extension talking to the headless server with a paired token (D2): everything the native
    /// host lets through, plus <c>folders.browse</c>, which only the server answers and which the extension's
    /// "ask where to save" needs there in place of the folder dialog. Nothing else; never <c>folders.create</c>.
    /// </summary>
    ServerExtension,
}

/// <summary>
/// What may cross between a bridge and the Host. The Host's pipe gives every client the same rights,
/// so this is the only thing keeping a bridge to what it needs. Both directions are allow-lists: a new
/// message type stays on the Host's side until it is added here on purpose.
/// </summary>
public static class NativeBridgePolicy
{
    private static readonly HashSet<string> FromExtensionTypes = new(StringComparer.Ordinal)
    {
        MessageTypes.HandoffRequest,
        MessageTypes.HostPing,
        MessageTypes.HostOpenUi,
        MessageTypes.HostPickFolder,
        MessageTypes.JobsQuery,
        // EXT.R2: the popup's actions on one download. job.remove may delete the file, as the app's own
        // Delete can; the extension asks only when the user ticks "Delete the file too".
        MessageTypes.JobPause,
        MessageTypes.JobResume,
        MessageTypes.JobRetry,
        MessageTypes.JobCancel,
        MessageTypes.JobRemove,
        // EXT.R1: only the default folder and the favorites, never the rest of the settings.
        MessageTypes.SettingsDownloadFolders,
        // EXT.R11: the popup's right-click menu, on one download each (see AllowFrom with the payload). job.update only
        // renames a finished file in its own folder; job.redownload never overwrites; job.reveal shows the file in
        // Explorer, from the native host only (the server's screen is not the user's).
        MessageTypes.JobReveal,
        MessageTypes.JobUpdate,
        MessageTypes.JobRedownload,
        // EXT.R13: the popup's double-click on a finished download, from the native host only like job.reveal. The Host
        // shows a program or script in its folder instead of starting it.
        MessageTypes.JobOpen,
    };

    private static readonly HashSet<string> ToExtensionTypes = new(StringComparer.Ordinal)
    {
        MessageTypes.ServerHello,
        MessageTypes.ServerIncompatible,
        MessageTypes.JobsSnapshot,
        MessageTypes.JobEvent,
        MessageTypes.JobActionResult,
        MessageTypes.HostActionResult,
        MessageTypes.HostFolderPicked,
        MessageTypes.HostExiting,
        MessageTypes.HandoffAck,
        MessageTypes.HandoffError,
        MessageTypes.SettingsDownloadFoldersResult,
    };

    /// <summary>
    /// UI.B24: the messages for which the Host shows a window: Explorer for job.reveal, a file (and the Windows security warning) for job.open, the folder dialog, the app's
    /// window. The browser is in front when the user asks, so the native host it started may set the foreground, and
    /// allows it to the Host before passing one of these on; otherwise the window opens behind the browser.
    /// </summary>
    public static bool ShowsWindow(string? messageType) =>
        messageType is MessageTypes.JobReveal or MessageTypes.JobOpen or MessageTypes.HostPickFolder or MessageTypes.HostOpenUi;

    public static bool AllowFrom(BridgeRole role, string? messageType) => role switch
    {
        BridgeRole.BrowserExtension => messageType is not null && FromExtensionTypes.Contains(messageType),
        BridgeRole.ServerExtension => messageType is not null && messageType is not (MessageTypes.JobReveal or MessageTypes.JobOpen) &&
            (FromExtensionTypes.Contains(messageType) || messageType == MessageTypes.FoldersBrowse),
        _ => false,
    };

    public static bool AllowTo(BridgeRole role, string? messageType) => role switch
    {
        BridgeRole.BrowserExtension => messageType is not null && ToExtensionTypes.Contains(messageType),
        BridgeRole.ServerExtension => messageType is not null &&
            (ToExtensionTypes.Contains(messageType) || messageType == MessageTypes.FoldersBrowseResult),
        _ => false,
    };

    public static bool AllowFromExtension(string? messageType) => AllowFrom(BridgeRole.BrowserExtension, messageType);

    // The job actions the popup sends, one download at a time.
    private static readonly HashSet<string> SingleJobActions = new(StringComparer.Ordinal)
    {
        MessageTypes.JobPause,
        MessageTypes.JobResume,
        MessageTypes.JobRetry,
        MessageTypes.JobCancel,
        MessageTypes.JobRemove,
    };

    /// <summary>
    /// Whether this message may cross, its payload included. The type alone is not enough for job
    /// actions: the Host's job.remove also takes a list of downloads, and job.cancel can delete the
    /// file. The popup acts on one download and deletes a file only with Delete, so from the extension
    /// a job action names exactly one download, carries no list, and asks to delete the file only on
    /// job.remove. A compromised extension then cannot empty the list and its files in one request.
    /// </summary>
    public static bool AllowFrom(BridgeRole role, IpcMessage message)
    {
        if (!AllowFrom(role, message.Type)) return false;
        if (ExactJobMessages.TryGetValue(message.Type, out var named)) return NamesExactly(message.Payload, named);
        if (role is not (BridgeRole.BrowserExtension or BridgeRole.ServerExtension) || !SingleJobActions.Contains(message.Type)) return true;
        if (message.Payload is not { ValueKind: JsonValueKind.Object } payload) return false;

        var namesOneJob = false;
        // The Host reads property names without regard to case, so every spelling counts here.
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name.Equals("jobIds", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Null &&
                    !(property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() == 0))
                {
                    return false;
                }
            }
            else if (property.Name.Equals("deleteFile", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.False &&
                    !(property.Value.ValueKind == JsonValueKind.True && message.Type == MessageTypes.JobRemove))
                {
                    return false;
                }
            }
            else if (property.Name.Equals("jobId", StringComparison.OrdinalIgnoreCase))
            {
                // Named twice is a forged request: which one the Host would read is not this check's to guess.
                if (namesOneJob) return false;
                namesOneJob = property.Value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(property.Value.GetString(), out var jobId) && jobId != Guid.Empty;
                if (!namesOneJob) return false;
            }
        }

        return namesOneJob;
    }

    public static bool AllowFromExtension(IpcMessage message) => AllowFrom(BridgeRole.BrowserExtension, message);

    // EXT.R11: messages whose payload must be exactly a jobId and, for job.update, a newFileName. The Host's job.update
    // also moves a file to any path, sets a completion program and more; from the extension it only renames.
    private static readonly Dictionary<string, string?> ExactJobMessages = new(StringComparer.Ordinal)
    {
        [MessageTypes.JobReveal] = null,
        [MessageTypes.JobOpen] = null,
        [MessageTypes.JobRedownload] = null,
        [MessageTypes.JobUpdate] = "newFileName",
    };

    /// <summary>
    /// An object with one <c>jobId</c> (a GUID, not empty) and, when <paramref name="nameProperty"/> is given, that one
    /// property as a non-empty string; nothing else, in any spelling. Whether the name is a good one is the Host's to say.
    /// </summary>
    private static bool NamesExactly(JsonElement? payload, string? nameProperty)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body) return false;
        var namesOneJob = false;
        var named = nameProperty is null;
        var seenName = false;
        foreach (var property in body.EnumerateObject())
        {
            if (property.Name.Equals("jobId", StringComparison.OrdinalIgnoreCase))
            {
                if (namesOneJob) return false;
                namesOneJob = property.Value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(property.Value.GetString(), out var jobId) && jobId != Guid.Empty;
                if (!namesOneJob) return false;
            }
            else if (nameProperty is not null && property.Name.Equals(nameProperty, StringComparison.OrdinalIgnoreCase))
            {
                if (seenName) return false;
                seenName = true;
                named = property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString());
                if (!named) return false;
            }
            else
            {
                return false;
            }
        }

        return namesOneJob && named;
    }

    public static bool AllowToExtension(string? messageType) => AllowTo(BridgeRole.BrowserExtension, messageType);

    /// <summary>
    /// The message as the extension may see it, or null when it may not see it at all. A snapshot
    /// loses its settings: the extension reads only the jobs, and the settings hold recent
    /// addresses, site logins, the proxy and package keys. Every download it carries, in a snapshot,
    /// an event, an action's answer or a handoff's acknowledgement, is shown as <see cref="ForBridge"/> says.
    /// </summary>
    public static IpcMessage? ToExtension(IpcMessage message) => To(BridgeRole.BrowserExtension, message);

    public static IpcMessage? To(BridgeRole role, IpcMessage message)
    {
        if (!AllowTo(role, message.Type)) return null;
        if (message.Payload is not { ValueKind: JsonValueKind.Object } payload) return message;
        if (message.Type == MessageTypes.JobsSnapshot)
        {
            var kept = new JsonObject();
            foreach (var property in payload.EnumerateObject())
            {
                if (property.Name.Equals("settings", StringComparison.OrdinalIgnoreCase)) continue;
                var value = JsonNode.Parse(property.Value.GetRawText());
                if (property.Name.Equals("jobs", StringComparison.OrdinalIgnoreCase) && value is JsonArray jobs)
                {
                    foreach (var job in jobs)
                    {
                        if (job is JsonObject one) ForBridge(one);
                    }
                }

                kept[property.Name] = value;
            }

            return message with { Payload = JsonSerializer.SerializeToElement(kept) };
        }

        // One download in an event, an action's answer or a handoff's acknowledgement. Rewritten only when it needs it,
        // so the many progress events of ordinary downloads pass as they are.
        if (message.Type is not (MessageTypes.JobEvent or MessageTypes.JobActionResult or MessageTypes.HandoffAck)) return message;
        var rewrite = false;
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name.Equals("job", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object &&
                NeedsRewrite(property.Value))
            {
                rewrite = true;
            }
        }

        if (!rewrite || JsonNode.Parse(payload.GetRawText()) is not JsonObject whole) return message;
        // Every spelling of "job" is rewritten: which one a client reads is not this check's to guess.
        var managed = false;
        foreach (var pair in whole.ToList())
        {
            if (pair.Key.Equals("job", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonObject single) managed |= ForBridge(single);
        }

        // The Host's refusal for such a download names what it is; the browser gets neutral words (rule 5).
        foreach (var pair in whole.ToList())
        {
            if (managed && pair.Key.Equals("error", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue error &&
                error.TryGetValue<string>(out var said) && !string.IsNullOrWhiteSpace(said))
            {
                whole[pair.Key] = JsonValue.Create(AppManagedError);
            }
        }

        return message with { Payload = JsonSerializer.SerializeToElement(whole) };
    }

    private static bool NeedsRewrite(JsonElement job)
    {
        foreach (var property in job.EnumerateObject())
        {
            if (property.Name.Equals(TorrentProperty, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                return true;
            // T10: only a finished .torrent download carries it (it is left out when false).
            if (property.Name.Equals(TorrentFileProperty, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private const string TorrentProperty = "torrent";

    /// <summary>T10: <see cref="JobDto.TorrentFileReady"/>, which no browser bridge is sent.</summary>
    private const string TorrentFileProperty = "torrentFileReady";

    /// <summary>
    /// Set on a download a browser bridge shows (see <see cref="ForBridge"/>) whose files only the app manages: the
    /// popup offers no rename or redownload for it. Generic on purpose: the extension says nothing of what it is.
    /// </summary>
    public const string AppManagedProperty = "appManaged";

    /// <summary>The failure text a browser bridge shows for a download only the app manages (the Host's names what it is).</summary>
    public const string AppManagedFailure = "Stopped. See the app for details.";

    /// <summary>The refusal text a browser bridge shows for an action on a download only the app manages.</summary>
    public const string AppManagedError = "The app did not do that. See the app for details.";

    /// <summary>
    /// A download as a browser bridge shows it (torrent wave B; roadmap rule 5 keeps torrents out of the extension). A
    /// torrent job's address is the short magnet the window shows (its info hash only: no trackers, no name), or empty
    /// before the hash is known, and it has no final address. A paused one that waits for the user's choice of files
    /// can't be resumed from the browser, as the engine would only hold it again: canResume is false, and the popup
    /// offers to open the app instead (a stopped one keeps Retry, which brings it back to that wait). Its failure text
    /// and status note, which the Host words for the app, become neutral or go, and its torrent details are not sent at
    /// all: the extension reads none of them. It is marked <see cref="AppManagedProperty"/>. Any other download is left
    /// as it is. Returns whether the download was one only the app manages.
    /// </summary>
    public static bool ForBridge(JsonObject job)
    {
        // T10: a finished download that is a .torrent file stays an ordinary download in the browser.
        Remove(job, TorrentFileProperty);
        if (Find(job, TorrentProperty) is not JsonObject torrent) return false;
        var hash = Find(torrent, "infoHash") is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var shortAddress = !string.IsNullOrEmpty(hash) && hash.All(char.IsAsciiLetterOrDigit) ? "magnet:?xt=urn:btih:" + hash : string.Empty;
        Set(job, "url", JsonValue.Create(shortAddress));
        Set(job, "finalUrl", null);
        var paused = Find(job, "state") is JsonValue state && state.TryGetValue<string>(out var stateText) &&
            stateText.Equals("paused", StringComparison.OrdinalIgnoreCase);
        if (paused && Find(torrent, "awaitingFileChoice") is JsonValue waiting && waiting.TryGetValue<bool>(out var waits) && waits)
            Set(job, "canResume", JsonValue.Create(false));
        foreach (var pair in job.ToList())
        {
            if (!pair.Key.Equals("failure", StringComparison.OrdinalIgnoreCase) || pair.Value is not JsonObject failure) continue;
            foreach (var field in failure.ToList())
            {
                if (field.Key.Equals("message", StringComparison.OrdinalIgnoreCase) && field.Value is JsonValue message &&
                    message.TryGetValue<string>(out var said) && !string.IsNullOrWhiteSpace(said))
                {
                    failure[field.Key] = JsonValue.Create(AppManagedFailure);
                }
            }
        }

        Remove(job, "statusNote");
        Remove(job, TorrentProperty);
        Set(job, AppManagedProperty, JsonValue.Create(true));
        return true;
    }

    /// <summary>Removes the property under every spelling it has.</summary>
    private static void Remove(JsonObject node, string name)
    {
        foreach (var key in node.Select(pair => pair.Key).Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
            node.Remove(key);
    }

    /// <summary>Sets the property under every spelling it has (the Host reads names without regard to case), or adds it.</summary>
    private static void Set(JsonObject node, string name, JsonNode? value)
    {
        var keys = node.Select(pair => pair.Key).Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keys.Count == 0) keys.Add(name);
        foreach (var key in keys) node[key] = value?.DeepClone();
    }

    private static JsonNode? Find(JsonObject node, string name)
        => node.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Chrome, Edge and Firefox refuse a native message over 1 MB and close the port (audit 3 M3).</summary>
    public const int BrowserMaxMessageBytes = 1024 * 1024;

    /// <summary>
    /// The message as it can go to the browser: itself when it fits in <see cref="BrowserMaxMessageBytes"/>; when it
    /// does not, a <c>handoff.error</c> answering the same request with a clear reason, so that request fails and the
    /// shared port stays open; null for a message that answers nothing (an event), which is dropped.
    /// </summary>
    public static IpcMessage? FitForBrowser(IpcMessage message, out int size)
    {
        size = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options).Length;
        return size <= BrowserMaxMessageBytes ? message : TooLarge(message, size);
    }

    /// <summary>
    /// <see cref="FitForBrowser"/> as the frame's bytes (<see cref="IpcFraming.WriteFrameAsync"/>): the message is
    /// serialised once, to measure it and to send it (audit 3 N5). <paramref name="size"/> is the message's own size;
    /// null for an event that is dropped.
    /// </summary>
    public static byte[]? FrameForBrowser(IpcMessage message, out int size)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        size = json.Length;
        if (size <= BrowserMaxMessageBytes) return json;
        return TooLarge(message, size) is { } error ? JsonSerializer.SerializeToUtf8Bytes(error, IpcJson.Options) : null;
    }

    private static IpcMessage? TooLarge(IpcMessage message, int size)
    {
        if (string.IsNullOrEmpty(message.ReplyTo)) return null;
        return IpcMessage.Create(MessageTypes.HandoffError, new
        {
            errorCode = "message_too_large",
            error = $"The desktop app's answer ({size / 1024} KB) is larger than the browser accepts (1 MB). Update the extension, or open the app to see the list.",
        }, message.ReplyTo);
    }
}
