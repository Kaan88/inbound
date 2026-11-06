using System.Text.Json;
using System.Text.Json.Nodes;

namespace DownloadManager.Contracts;

/// <summary>
/// What may cross between the browser extension and the Host. The Host's pipe gives every client
/// the same rights, so this is the only thing keeping the extension to what it needs.
/// </summary>
public static class NativeBridgePolicy
{
    private static readonly HashSet<string> FromExtension = new(StringComparer.Ordinal)
    {
        MessageTypes.HandoffRequest,
        MessageTypes.HostPing,
        MessageTypes.HostOpenUi,
        MessageTypes.HostPickFolder,
        MessageTypes.JobsQuery,
    };

    public static bool AllowFromExtension(string? messageType)
        => messageType is not null && FromExtension.Contains(messageType);

    public static bool AllowToExtension(string? messageType)
        => messageType is null ||
           (!messageType.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) &&
            !messageType.StartsWith("archive.", StringComparison.OrdinalIgnoreCase) &&
            !messageType.Equals(MessageTypes.GroupsChanged, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The message as the extension may see it, or null when it may not see it at all. A snapshot
    /// loses its settings: the extension reads only the jobs, and the settings hold recent
    /// addresses, site logins, the proxy and package keys.
    /// </summary>
    public static IpcMessage? ToExtension(IpcMessage message)
    {
        if (!AllowToExtension(message.Type)) return null;
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
