using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What a file selection dialog was given to show.</summary>
internal sealed record RemoteFilesToShow(IReadOnlyList<RemoteFileDto> Files, bool Pending = false, string? Error = null, string? Title = null);

/// <summary>What came of a file selection dialog: files chosen, or to be chosen once the provider knows them. Neither means it was cancelled, or with <paramref name="CouldNotOpen"/> that it never showed.</summary>
internal sealed record RemoteFileAnswer(RemoteFileChoice? Choice, bool Later, bool CouldNotOpen = false);

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
        => (await AskAsync(root, theme, title, providerName, load, primaryText)).Choice;

    /// <param name="load">Reads the files. Runs while the dialog is open, on its own connection.</param>
    /// <param name="laterText">Offered while the provider doesn't know the files yet, for going ahead and choosing them when it does. Null offers nothing.</param>
    public static async Task<RemoteFileAnswer> AskAsync(
        XamlRoot root,
        ElementTheme theme,
        string title,
        string providerName,
        Func<CancellationToken, Task<RemoteFilesToShow>> load,
        string primaryText = "Download selected",
        string closeText = "Cancel",
        string? laterText = null)
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
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,

            // A default button that starts disabled doesn't pick up the accent by itself.
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        tree.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = tree.Tree.ChosenCount > 0;

        using var closing = new CancellationTokenSource();
        try
        {
            // This call is what throws when another dialog is open, and then nothing has been asked yet.
            var shown = dialog.ShowAsync();
            _ = FillAsync(tree, busy, status, providerName, load, laterText is null ? null : () => dialog.SecondaryButtonText = laterText, laterText, title => dialog.Title = title, closing.Token);
            var answer = await shown;
            closing.Cancel();
            return new RemoteFileAnswer(answer == ContentDialogResult.Primary && tree.Tree.ChosenCount > 0 ? tree.Tree.Choice : null, answer == ContentDialogResult.Secondary);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open over this window.
            closing.Cancel();
            return new RemoteFileAnswer(null, false, CouldNotOpen: true);
        }
    }

    private static async Task FillAsync(
        RemoteFileTreeView tree,
        ProgressRing busy,
        StatusLine status,
        string providerName,
        Func<CancellationToken, Task<RemoteFilesToShow>> load,
        Action? offerLater,
        string? laterText,
        Action<string> setTitle,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await load(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            busy.IsActive = false;
            busy.Visibility = Visibility.Collapsed;
            if (result.Title is { } title) setTitle(title);
            if (result.Error is { } error)
            {
                tree.ShowMessage(string.Empty);
                status.Show(RemoteText.Failure(error), isProblem: true);
            }
            else if (result.Pending && result.Files.Count == 0)
            {
                tree.ShowMessage(string.Empty);
                offerLater?.Invoke();
                status.Show(laterText is null
                    ? $"{providerName} doesn't know the files yet. Try again when it does."
                    : $"{providerName} doesn't know the files yet. Select \"{laterText}\" to choose them when it does.");
            }
            else if (result.Files.Count == 0)
            {
                tree.ShowMessage(string.Empty);
                status.Show($"{providerName} lists no files for this.");
            }
            else
            {
                tree.SetFiles(result.Files, chooseAll: true);
                if (result.Pending) offerLater?.Invoke();
                status.Show(result.Pending ? $"{providerName} is still working on this, so the list may change." : string.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog closed first, and there is nothing left to show it in.
        }
    }
}
