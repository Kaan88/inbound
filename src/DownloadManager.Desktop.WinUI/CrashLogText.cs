using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>An exception's text can quote an address, and a provider's direct link holds the account key.</summary>
internal static class CrashLogText
{
    public static string Entry(DateTimeOffset at, object? exception)
        => $"{at:O}{Environment.NewLine}{UrlRedactor.RedactText(exception?.ToString())}{Environment.NewLine}";
}
