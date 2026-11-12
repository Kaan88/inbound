using DownloadManager.Contracts;
using DownloadManager.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UPD.N1: What's new. It opens by itself once, at the first window start after the installed version changed
/// (<see cref="WhatsNewRules"/>), and again from Help > What's New… or the version in the status bar. It is about this
/// PC's app only: a server shown in the window (D9) has its own notes in its web UI.
/// </summary>
public sealed partial class MainWindow
{
    private bool _whatsNewDecided;

    /// <summary>What's new is the Windows app's; the Linux window built from this code has none yet (out of UPD.N1).</summary>
    private static bool WhatsNewApplies => OperatingSystem.IsWindows();

    /// <summary>The version link in the status bar runs Check for updates (true) or What's new (false).</summary>
    private bool _versionLinkUpdates;

    /// <summary>The installed version; for a development build, the build's own (Help still shows its notes).</summary>
    private static string ThisAppVersion()
        => InstalledBuild.TryLoad()?.Version ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private ChangelogFeatures WhatsNewFeatures()
    {
        // Provider lines show for the providers with an account set (a line tagged with the provider's id).
        var providers = _remote.State.Accounts.Where(RemoteRules.HasUsableKey)
            .Select(account => account.Provider.ToLowerInvariant()).ToList();
        // Shown for another server, this PC's torrent setting isn't the one the window has: torrent lines show.
        return new ChangelogFeatures(_connection.IsRemote || LocalTorrentsApply, providers);
    }

    /// <summary>After this PC's first snapshot: opens What's new when the installed version changed since the last start.</summary>
    private async void MaybeOpenWhatsNewAtStart()
    {
        if (_whatsNewDecided || _connection.IsRemote) return;
        _whatsNewDecided = true;
        // The Linux window (the same code under Uno) has no What's new yet: its lines are tagged linux, which this hides.
        if (!WhatsNewApplies) return;
        try
        {
            var decision = WhatsNewRules.Decide(InstalledBuild.TryLoad()?.Version, _uiState.WhatsNewSeen,
                UiStateStore.ExistedAtStart || _jobsById.Count > 0);
            UiTrace.Write($"What's new at start: installed={InstalledBuild.TryLoad()?.Version ?? "-"} seen={_uiState.WhatsNewSeen ?? "-"} " +
                          $"open={decision.Open} after={decision.After ?? "-"} record={decision.Record ?? "-"}");
            if (!decision.Open)
            {
                if (decision.Record is { } record) RecordWhatsNewSeen(record);
                return;
            }

            // Provider lines show only with an account, which the Host reports a moment after the snapshot.
            for (var wait = 0; wait < 12 && !_remote.State.AccountsKnown; wait++) await Task.Delay(250);
            // The first snapshot can come before the window has loaded, and a dialog can't show without its XamlRoot.
            for (var wait = 0; wait < 40 && (RootGrid.XamlRoot is null || !RootGrid.IsLoaded); wait++) await Task.Delay(250);
            var current = decision.Record!;
            var versions = Changelog.Between(Changelog.Embedded, decision.After, current);
            var view = ChangelogFilter.For(versions, ChangelogSurface.Windows, WhatsNewFeatures());
            var previous = _uiState.WhatsNewSeen;
            // Kept before it shows, so a window that closes or fails while it is open doesn't show it again.
            RecordWhatsNewSeen(current);
            if (view.IsEmpty) return;
            var subtitle = WhatsNewRules.Subtitle(decision.After, decision.AfterKnown, Changelog.Find(Changelog.Embedded, current)?.Date);
            if (await ShowWhatsNewAsync(current, subtitle, view) is null)
            {
                // Another dialog was open: nothing was shown, so the next start tries again.
                UiTrace.Write("What's new at start was not shown: another dialog was open or the window wasn't ready.");
                RecordWhatsNewSeen(previous);
            }
        }
        catch (Exception ex)
        {
            UiTrace.Write($"What's new at start failed: {ex.Message}");
        }
    }

    private void RecordWhatsNewSeen(string? version)
    {
        if (_uiState.WhatsNewSeen == version) return;
        _uiState.WhatsNewSeen = version;
        UiStateStore.Save(_uiState);
    }

    private void MenuWhatsNew_Click(object sender, RoutedEventArgs e) => _ = ShowInstalledWhatsNewAsync();

    /// <summary>Help > What's New… and the status bar's version: the installed version's notes.</summary>
    private async Task ShowInstalledWhatsNewAsync()
    {
        var version = ThisAppVersion();
        var entry = Changelog.Find(Changelog.Embedded, version);
        var view = entry is null ? null : ChangelogFilter.For([entry], ChangelogSurface.Windows, WhatsNewFeatures());
        if (view is null || view.IsEmpty)
        {
            await ShowMessageAsync("What's new", $"There are no notes about this app for version {Changelog.Display(version)}.");
            return;
        }

        await ShowWhatsNewAsync(Changelog.Display(version), WhatsNewRules.Subtitle(null, false, entry!.Date), view);
    }

    private Task<ContentDialogResult?> ShowWhatsNewAsync(string version, string subtitle, ChangelogView view)
    {
        var theme = RootGrid.ActualTheme;
        var muted = InboundTheme.Brush("InboundSecondaryTextBrush", theme);
        var notes = new StackPanel { Spacing = 4 };
        var several = view.Versions.Count > 1;
        foreach (var shown in view.Versions)
        {
            if (several)
            {
                var day = Changelog.DisplayDate(shown.Date);
                notes.Children.Add(new TextBlock
                {
                    Text = day is null ? Changelog.Display(shown.Version) : $"{Changelog.Display(shown.Version)} · {day}",
                    FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, notes.Children.Count == 0 ? 0 : 14, 0, 0),
                });
            }

            foreach (var section in shown.Sections)
            {
                notes.Children.Add(new TextBlock
                {
                    Text = section.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, notes.Children.Count == 0 ? 0 : 10, 0, 2),
                });
                foreach (var line in section.Lines) notes.Children.Add(Bullet(line));
            }
        }

        if (view.Elsewhere.Count > 0)
        {
            var folded = new StackPanel { Spacing = 4 };
            foreach (var line in view.Elsewhere)
            {
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                text.Inlines.Add(new Run { Text = $"{line.Place} · ", Foreground = muted });
                text.Inlines.Add(new Run { Text = line.Text });
                folded.Children.Add(text);
            }

            notes.Children.Add(new Expander
            {
                Header = view.ElsewhereText,
                Content = folded,
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 14, 0, 0),
            });
        }

        var body = new StackPanel { Spacing = 8 };
        if (subtitle.Length > 0) body.Children.Add(new TextBlock { Text = subtitle, Foreground = muted, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer
        {
            Padding = InboundTheme.DialogScrollPadding,
            Content = notes,
            MaxHeight = Math.Max(200, Math.Min(460, RootGrid.ActualHeight - 260)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        body.Children.Add(ReleaseNotesLink(version));

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = theme,
            Title = $"What's new in Inbound {version}",
            Content = body,
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary,
        };
        return ShowDialogAsync(dialog);
    }

    private static Grid Bullet(string text)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = "•" });
        var words = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(words, 1);
        row.Children.Add(words);
        return row;
    }

    private static HyperlinkButton ReleaseNotesLink(string version) => new()
    {
        Content = "Full release notes",
        NavigateUri = new Uri(Changelog.ReleaseNotesUrl(version)),
        Padding = new Thickness(0, 2, 0, 2),
    };

    /// <summary>
    /// The Update available question's notes (UPD.N1): this app's lines of the new version in a box, then how many the
    /// other places have. Null when the manifest brought none for this app, so the question looks as it did before.
    /// </summary>
    private FrameworkElement? UpdateNotesBox(UpdateCheckResult check)
    {
        var words = WhatsNewRules.UpdateNotes(check.Notes, check.AvailableVersion, WhatsNewFeatures(), WhatsNewApplies);
        if (words.Header is null) return null;
        var theme = RootGrid.ActualTheme;
        var lines = new StackPanel { Spacing = 4 };
        foreach (var line in words.Lines) lines.Children.Add(Bullet(line));
        var box = new StackPanel { Spacing = 6 };
        box.Children.Add(new TextBlock { Text = words.Header, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        box.Children.Add(new Border
        {
            BorderBrush = InboundTheme.Brush("InboundDividerBrush", theme),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 4, 8),
            Child = new ScrollViewer
            {
                Content = lines,
                Padding = InboundTheme.DialogScrollPadding,
                MaxHeight = Math.Max(120, Math.Min(240, RootGrid.ActualHeight - 360)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        });
        if (words.Also is { } also)
            box.Children.Add(new TextBlock { Text = also, Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", theme), TextWrapping = TextWrapping.Wrap });
        return box;
    }
}
