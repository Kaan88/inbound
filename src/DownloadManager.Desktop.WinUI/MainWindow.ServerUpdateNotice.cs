using DownloadManager.Contracts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UPD.D1: the shown Docker server has a newer image. The window shows a bar over the list ("… can be updated to …") with
/// How to update and Not now, and the dialog behind How to update: the pull/restart commands with Copy, the server's own
/// changelog lines collapsed, Skip this version and Close. The dismiss state is kept on the server, so the web UI and any
/// paired window agree; a window also hears another session's dismiss as the <c>update.notice</c> push. A Docker server is
/// never updated from here, so the dialog only tells where and how.
/// </summary>
public sealed partial class MainWindow
{
    private const string DockerComposeCommand = "docker compose pull inbound && docker compose up -d inbound";
    private const string DockerRunCommand = "docker pull ghcr.io/kaan88/inbound:latest";

    private bool ServerUpdateNoticeShows()
    {
        // Only the Docker server kind gets this bar: a Linux server updates through its own app, a Windows peer through
        // its own window. And only while the graph is connected enough to trust the version it shows.
        if (!_connection.IsRemote || !_graphConnected) return false;
        if (_connection.Hello?.HostKind != HostKinds.Server) return false;
        if (VersionStatus.Compare(_status?.HostVersion, ShownServerLatest) is not < 0) return false;

        var now = DateTimeOffset.UtcNow;
        if (_status?.UpdateNoticeHiddenUntil is { } hidden && hidden > now) return false;
        if (_status?.UpdateNoticeSkippedVersion is { } skipped && VersionStatus.Compare(ShownServerLatest, skipped) is not > 0) return false;
        if (_connection.Target.Id is { } id && _localNoticeDismissals.TryGetValue(id, out var local))
        {
            if (local.HiddenUntil is { } until && until > now) return false;
            if (local.SkippedVersion is { } kept && VersionStatus.Compare(ShownServerLatest, kept) is not > 0) return false;
        }
        return true;
    }

    /// <summary>
    /// A dismiss the server could not keep (a server older than 0.9.1 doesn't know <c>update.dismiss</c>, or it couldn't be
    /// reached), kept by this window per saved server for as long as it runs, so the buttons never do nothing.
    /// </summary>
    private readonly Dictionary<string, (DateTimeOffset? HiddenUntil, string? SkippedVersion)> _localNoticeDismissals = new(StringComparer.Ordinal);

    /// <summary>Shows or hides the bar to match what the shown server knows, and writes its words.</summary>
    private void UpdateServerNotice()
    {
        if (ServerUpdateNoticeShows())
        {
            var name = _connection.Target.Name;
            var current = Changelog.Display(_status!.HostVersion!);
            var latest = Changelog.Display(ShownServerLatest!);
            ServerUpdateNoticeText.Inlines.Clear();
            ServerUpdateNoticeText.Inlines.Add(new Run
            {
                Text = $"{name} can be updated to {latest}",
                FontWeight = FontWeights.SemiBold,
            });
            ServerUpdateNoticeText.Inlines.Add(new Run
            {
                Text = $"  It runs {current}. A Docker server is updated where it runs: pull the new image and restart the container.",
            });
            ServerUpdateNoticeBar.Visibility = Visibility.Visible;
        }
        else
        {
            ServerUpdateNoticeBar.Visibility = Visibility.Collapsed;
        }
    }

    // On a window too narrow for the words and both buttons on one line, the buttons go below the words (UI.B29's rule).
    private void ServerUpdateNoticeBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 620;
        Grid.SetRow(ServerUpdateNoticeButtons, narrow ? 1 : 0);
        Grid.SetColumn(ServerUpdateNoticeButtons, narrow ? 1 : 2);
        ServerUpdateNoticeButtons.HorizontalAlignment = narrow ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    }

    private void ServerUpdateNoticeHow_Click(object sender, RoutedEventArgs e) => _ = ShowServerUpdateDialogAsync();

    private void ServerUpdateNoticeLater_Click(object sender, RoutedEventArgs e) => _ = DismissServerNoticeAsync(UpdateDismissActions.NotNow);

    /// <summary>
    /// Sends the dismiss to the server, with the version shown, and hides the bar. The server's push updates
    /// <see cref="_status"/> too; the local update keeps the bar gone at once without waiting for it. When the server
    /// doesn't take it (older than 0.9.1, or out of reach), this window keeps the dismiss itself.
    /// </summary>
    private async Task DismissServerNoticeAsync(string action)
    {
        var shownLatest = ShownServerLatest;
        var serverId = _connection.Target.Id;
        var kept = false;
        try
        {
            var reply = await _connection.DismissUpdateNoticeAsync(action, shownLatest);
            kept = reply.GetPayload<HostActionResult>() is { Ok: true };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException or InvalidDataException)
        {
            // Out of reach: kept here below.
        }

        var hiddenUntil = action == UpdateDismissActions.NotNow ? DateTimeOffset.UtcNow.AddDays(1) : (DateTimeOffset?)null;
        var skipped = action == UpdateDismissActions.Skip ? shownLatest : null;
        if (kept && _status is { } shown)
        {
            if (hiddenUntil is not null) shown.UpdateNoticeHiddenUntil = hiddenUntil;
            if (skipped is not null) shown.UpdateNoticeSkippedVersion = skipped;
        }
        else if (!kept && serverId is not null)
        {
            _localNoticeDismissals.TryGetValue(serverId, out var local);
            _localNoticeDismissals[serverId] = (hiddenUntil ?? local.HiddenUntil, skipped ?? local.SkippedVersion);
        }

        UpdateServerNotice();
    }

    private async Task ShowServerUpdateDialogAsync()
    {
        var name = _connection.Target.Name;
        var current = Changelog.Display(_status?.HostVersion ?? string.Empty);
        var latest = Changelog.Display(ShownServerLatest ?? string.Empty);

        ServerUpdateNotesResult? notes = null;
        try
        {
            notes = (await _connection.ServerUpdateNotesAsync()).GetPayload<ServerUpdateNotesResult>();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException or InvalidDataException)
        {
            // The commands and the version still show; only the changelog lines are missing.
        }

        var body = new StackPanel { Spacing = 10, MinWidth = 460 };
        body.Children.Add(Wrapped($"{current} → {latest}" +
            (Changelog.DisplayDate(notes?.ReleaseDate) is { } day ? $" · released {day}" : string.Empty)));

        var how = InboundUi.Secondary("Inbound can't update a Docker server from here. On the machine that runs it, pull the new image and start the container again. Its downloads and settings stay in its data volume, and this window reconnects by itself.");
        how.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(how);

        body.Children.Add(CommandRow("Compose", DockerComposeCommand));
        body.Children.Add(CommandRow("docker run", DockerRunCommand));

        var (here, _) = ChangelogFilter.Lines(notes?.Notes ?? [], ChangelogSurface.WebUi, WhatsNewFeatures());
        if (here.Count > 0) body.Children.Add(ChangelogExpander(here));

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = $"Update {name}",
            Content = body,
            // DesktopDialog board: "Skip this version | Close", Close the default.
            PrimaryButtonText = "Skip this version",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary) await DismissServerNoticeAsync(UpdateDismissActions.Skip);
    }

    /// <summary>A command row: its name, the command, and a Copy button that briefly says Copied.</summary>
    private UIElement CommandRow(string label, string command)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = InboundUi.Secondary(label);
        name.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(name);

        var code = new TextBlock
        {
            Text = command,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(code);

        var copy = new Button { Content = "Copy", FontSize = 12, Padding = new Thickness(10, 2, 10, 2) };
        Grid.SetColumn(code, 1);
        Grid.SetColumn(copy, 2);
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(command);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                copy.Content = "Copied";
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                copy.Content = "Copy";
            }
        };
        grid.Children.Add(copy);
        return grid;
    }

    private static UIElement ChangelogExpander(IReadOnlyList<string> lines)
    {
        var list = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var line in lines)
        {
            var row = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap };
            list.Children.Add(row);
        }

        return new Expander
        {
            Header = $"What changes for the server ({lines.Count})",
            Content = list,
        };
    }
}
