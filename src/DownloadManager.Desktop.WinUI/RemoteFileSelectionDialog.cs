using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What a file selection dialog was given to show.</summary>
internal sealed record RemoteFilesToShow(IReadOnlyList<RemoteFileDto> Files, bool Pending = false, string? Error = null);

/// <summary>
/// Which of a provider item's files to download: a tree with select all and none, sizes, and files that
/// can't be chosen on show with their note. Used wherever files get chosen. It opens straight away and
/// fills in when the files arrive, and closing it ends that call by closing its connection.
/// </summary>
internal static class RemoteFileSelectionDialog
{
    /// <param name="load">Reads the files. Runs while the dialog is open, on its own connection.</param>
    /// <returns>The choice, or null when it was cancelled or nothing could be chosen.</returns>
    public static async Task<RemoteFileChoice?> ShowAsync(
        XamlRoot root,
        ElementTheme theme,
        string title,
        string providerName,
        Func<CancellationToken, Task<RemoteFilesToShow>> load,
        string primaryText = "Download selected")
    {
        var tree = new RemoteFileTreeView { Height = 380 };
        var busy = new ProgressRing { IsActive = true, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(busy, $"Asking {providerName} for the files");
        var status = new StatusLine("Files status");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { busy, status.Text } };
        var panel = new StackPanel { Spacing = 8, MinWidth = 640, Children = { header, tree } };
        status.Show($"Asking {providerName} for the files…");

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        tree.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = tree.Tree.ChosenCount > 0;

        using var closing = new CancellationTokenSource();
        var shown = dialog.ShowAsync();
        _ = FillAsync(tree, busy, status, providerName, load, closing.Token);
        try
        {
            var answer = await shown;
            closing.Cancel();
            return answer == ContentDialogResult.Primary && tree.Tree.ChosenCount > 0 ? tree.Tree.Choice : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open over this window.
            closing.Cancel();
            return null;
        }
    }

    private static async Task FillAsync(
        RemoteFileTreeView tree,
        ProgressRing busy,
        StatusLine status,
        string providerName,
        Func<CancellationToken, Task<RemoteFilesToShow>> load,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await load(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            busy.IsActive = false;
            busy.Visibility = Visibility.Collapsed;
            if (result.Error is { } error)
            {
                tree.ShowMessage(string.Empty);
                status.Show(RemoteText.Failure(error), isProblem: true);
            }
            else if (result.Pending && result.Files.Count == 0)
            {
                tree.ShowMessage(string.Empty);
                status.Show($"{providerName} doesn't know the files yet. Try again when it does.");
            }
            else if (result.Files.Count == 0)
            {
                tree.ShowMessage(string.Empty);
                status.Show($"{providerName} lists no files for this.");
            }
            else
            {
                tree.SetFiles(result.Files, chooseAll: true);
                status.Show(result.Pending ? $"{providerName} is still working on this, so the list may change." : string.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog closed first, and there is nothing left to show it in.
        }
    }
}
