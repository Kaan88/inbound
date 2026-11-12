using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What a file selection dialog was given to show.</summary>
/// <param name="Chosen">Ticked to begin with, such as what was found of an earlier choice. Null or empty ticks everything.</param>
internal sealed record RemoteFilesToShow(IReadOnlyList<RemoteFileDto> Files, bool Pending = false, string? Error = null, string? Title = null, IReadOnlyList<string>? Chosen = null);

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
    /// <param name="laterText">Offered while the provider doesn't know the files yet, for going ahead without them. Null offers nothing.</param>
    /// <param name="laterWords">What that button does, said when there are no files to show. Null says it chooses them later.</param>
    /// <param name="loadingText">Said while the files are read, for a list that comes from no provider (a torrent). Null names the provider.</param>
    public static async Task<RemoteFileAnswer> AskAsync(
        XamlRoot root,
        ElementTheme theme,
        string title,
        string providerName,
        Func<CancellationToken, Task<RemoteFilesToShow>> load,
        string primaryText = "Download selected",
        string closeText = "Cancel",
        string? laterText = null,
        string? laterWords = null,
        string? loadingText = null)
    {
        var tree = new RemoteFileTreeView { Height = 380 };
        var busy = new ProgressRing { IsActive = true, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(busy, loadingText?.TrimEnd('…') ?? $"Asking {providerName} for the files");
        var status = new StatusLine("Files status");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { busy, status.Text } };
        var panel = new StackPanel { Spacing = 8, MinWidth = 640, Children = { header, tree } };
        status.Show(loadingText ?? $"Asking {providerName} for the files…");

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
        // Refused at once, without showing, when another dialog is open over this window (UI.B8); nothing has been
        // asked yet then, so nothing is loaded either.
        var shown = DialogGuard.ShowAsync(dialog);
        if (shown.IsCompleted && shown.Result is null) return new RemoteFileAnswer(null, false, CouldNotOpen: true);
        {
            var laterSaid = laterText is null ? null : laterWords ?? $"Select \"{laterText}\" to choose them when it does.";
            _ = FillAsync(tree, busy, status, providerName, load, laterText is null ? null : () => dialog.SecondaryButtonText = laterText, laterSaid, title => dialog.Title = title, closing.Token);
            var answer = await shown;
            closing.Cancel();
            if (answer is null) return new RemoteFileAnswer(null, false, CouldNotOpen: true);
            return new RemoteFileAnswer(answer == ContentDialogResult.Primary && tree.Tree.ChosenCount > 0 ? tree.Tree.Choice : null, answer == ContentDialogResult.Secondary);
        }
    }

    private static async Task FillAsync(
        RemoteFileTreeView tree,
        ProgressRing busy,
        StatusLine status,
        string providerName,
        Func<CancellationToken, Task<RemoteFilesToShow>> load,
        Action? offerLater,
        string? laterSaid,
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
                status.Show(laterSaid is null
                    ? $"{providerName} doesn't know the files yet. Try again when it does."
                    : $"{providerName} doesn't know the files yet. {laterSaid}");
            }
            else if (result.Files.Count == 0)
            {
                tree.ShowMessage(string.Empty);
                status.Show($"{providerName} lists no files for this.");
            }
            else
            {
                if (result.Chosen is { Count: > 0 } chosen) tree.SetFiles(result.Files, keep: chosen);
                else tree.SetFiles(result.Files, chooseAll: true);
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
