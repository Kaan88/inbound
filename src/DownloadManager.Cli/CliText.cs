using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Cli;

/// <summary>How the CLI shows text it did not write, and a Host's error in a reply that is not the result it asked for.</summary>
internal static class CliText
{
    /// <summary>Text from the Host or the user, with what a terminal would act on (escape sequences, bidi marks) made spaces.</summary>
    internal static string Printable(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty
            : text.Any(FileNameResolver.IsDeceptiveOrControl)
                ? new string(text.Select(c => FileNameResolver.IsDeceptiveOrControl(c) ? ' ' : c).ToArray())
                : text;

    /// <summary>
    /// For what goes to the screen from a provider's answer: addresses are hidden, and what a terminal would act on
    /// rather than show is made spaces, so it can't rewrite what was printed above it.
    /// </summary>
    internal static string Clean(string? text) => Printable(UrlRedactor.HideAddresses(text));

    /// <summary>
    /// The Host's own error in a reply that is not the expected result, made printable; null for a Host that doesn't
    /// know the command ("Unknown message type", an older Host or one without torrents) or a reply that isn't an error
    /// (audit 4 M12).
    /// </summary>
    internal static string? HostError(IpcMessage reply)
    {
        if (reply.Type != MessageTypes.HostActionResult) return null;
        string? message;
        try { message = reply.GetPayload<HostActionResult>()?.Message; }
        catch (JsonException) { return null; }
        return string.IsNullOrWhiteSpace(message) || message.StartsWith("Unknown message type", StringComparison.Ordinal)
            ? null
            : Printable(message);
    }
}
