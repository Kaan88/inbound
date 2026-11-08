using System.Text.Json;
using System.Text.Json.Nodes;

namespace DownloadManager.Contracts;

/// <summary>Who is on the far side of a bridge into the Host, which decides what may cross it.</summary>
public enum BridgeRole
{
    /// <summary>The browser extension, through the native-messaging host.</summary>
    BrowserExtension,
    // The server's bridge (PLAN.md stream C transport and D2) adds its own role here, with what only
    // it may send, such as browsing the server's folders.
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

    public static bool AllowFrom(BridgeRole role, string? messageType) => role switch
    {
        BridgeRole.BrowserExtension => messageType is not null && FromExtensionTypes.Contains(messageType),
        _ => false,
    };

    public static bool AllowTo(BridgeRole role, string? messageType) => role switch
    {
        BridgeRole.BrowserExtension => messageType is not null && ToExtensionTypes.Contains(messageType),
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
        if (role != BridgeRole.BrowserExtension || !SingleJobActions.Contains(message.Type)) return true;
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

    public static bool AllowToExtension(string? messageType) => AllowTo(BridgeRole.BrowserExtension, messageType);

    /// <summary>
    /// The message as the extension may see it, or null when it may not see it at all. A snapshot
    /// loses its settings: the extension reads only the jobs, and the settings hold recent
    /// addresses, site logins, the proxy and package keys.
    /// </summary>
    public static IpcMessage? ToExtension(IpcMessage message) => To(BridgeRole.BrowserExtension, message);

    public static IpcMessage? To(BridgeRole role, IpcMessage message)
    {
        if (!AllowTo(role, message.Type)) return null;
        if (message.Type != MessageTypes.JobsSnapshot || message.Payload is not { ValueKind: JsonValueKind.Object } payload)
        {
            return message;
        }

        var kept = new JsonObject();
        foreach (var property in payload.EnumerateObject())
        {
            if (!property.Name.Equals("settings", StringComparison.OrdinalIgnoreCase))
            {
                kept[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return message with { Payload = JsonSerializer.SerializeToElement(kept) };
    }
}
