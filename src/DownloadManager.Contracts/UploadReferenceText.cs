namespace DownloadManager.Contracts;

/// <summary>
/// Words a server and the desktop app both rely on about an uploaded file's <c>upload:</c> reference. They are here, in the
/// shared contract, so the app can tell the answer apart without keeping its own copy of the sentence.
/// </summary>
public static class UploadReferenceText
{
    /// <summary>What a server says for a reference whose file has gone (cleaned up after a day, or never there).</summary>
    public const string Gone = "The uploaded file is no longer there. Upload it again.";

    /// <summary>The text says an upload's file is gone, so the reference must not be sent again.</summary>
    public static bool IsGone(string? text) => text is not null && text.Contains(Gone, StringComparison.Ordinal);
}
