using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R41 (review 5:F1): the ways to the browser extension from inside the window: the one-time bar over the list, Help >
/// Get the Browser Extension…, and the section in Options (General > Capturing downloads). The rules are in
/// <see cref="ExtensionPromoRules"/>.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The bar over the list is showing (UI.B29: a plain bar, so it is shown and hidden by its visibility).</summary>
    private bool ExtensionHintOpen => ExtensionHintBar.Visibility == Visibility.Visible;

    /// <param name="anyBrowserDownload">The list holds, or just got, a download the extension sent.</param>
    private void UpdateExtensionHint(bool anyBrowserDownload)
    {
        var decision = ExtensionPromoRules.Decide(_uiState.ExtensionHintDone, !_connection.IsRemote,
            _status?.BrowserIntegration, _status?.ExtensionsSeen, anyBrowserDownload);
        if (decision.MarkDone)
        {
            _uiState.ExtensionHintDone = true;
            UiStateStore.Save(_uiState);
        }

        if (decision.Show && !ExtensionHintOpen)
        {
            ExtensionHintText.Inlines.Clear();
            ExtensionHintText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = ExtensionPromoRules.HintTitle,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            ExtensionHintText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + ExtensionPromoRules.HintMessage });
            ExtensionHintBar.Visibility = Visibility.Visible;
            AnnounceExtensionHint();
        }
        else if (!decision.Show && ExtensionHintOpen)
        {
            CloseExtensionHint();
        }
    }

    /// <summary>
    /// The InfoBar read its title and message out when it opened; the plain bar asks its words' peer to (UI.B29). It waits
    /// for the next turn of the dispatcher so the bar, just made visible, has been laid out and is in the automation tree;
    /// the announcement is the full title and message, not a short name.
    /// </summary>
    private void AnnounceExtensionHint()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ExtensionHintOpen)
            {
                return;
            }

            try
            {
                Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(ExtensionHintText)
                    ?.RaiseNotificationEvent(
                        Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.Other,
                        Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
                        ExtensionPromoRules.HintTitle + ". " + ExtensionPromoRules.HintMessage,
                        "InboundExtensionHint");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Announcing is a courtesy; the bar is there either way.
            }
        });
    }

    /// <summary>
    /// UI.B29: on a window too narrow for the words and both buttons on one line, the buttons go below the words, together
    /// and on the right; otherwise they sit on the right of the one line.
    /// </summary>
    private void ExtensionHintBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < ExtensionPromoRules.HintOneLineMinWidth;
        Grid.SetRow(ExtensionHintButtons, narrow ? 1 : 0);
        Grid.SetColumn(ExtensionHintButtons, narrow ? 1 : 2);
        ExtensionHintButtons.HorizontalAlignment = narrow ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    }

    private void ExtensionHintLater_Click(object sender, RoutedEventArgs e)
    {
        _uiState.ExtensionHintDone = true;
        UiStateStore.Save(_uiState);
        CloseExtensionHint();
    }

    /// <summary>
    /// Closing the bar collapses its buttons, and a focused button that collapses drops focus, so the next Tab would start
    /// from the top of the window. Focus that was in the bar goes to the download list instead (review of 0.7.2, ux-6).
    /// </summary>
    private void CloseExtensionHint()
    {
        var hadFocus = FocusIsInExtensionHint();
        ExtensionHintBar.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (hadFocus || FocusIsInExtensionHint() ||
                (RootGrid.XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is null))
                JobsList.Focus(FocusState.Programmatic);
        });
    }

    private bool FocusIsInExtensionHint()
    {
        if (RootGrid.XamlRoot is not { } root) return false;
        for (var element = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as DependencyObject;
             element is not null;
             element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, ExtensionHintBar)) return true;
        }

        return false;
    }

    private void ExtensionHintGet_Click(object sender, RoutedEventArgs e) => _ = ShowGetExtensionAsync();

    private void MenuGetExtension_Click(object sender, RoutedEventArgs e) => _ = ShowGetExtensionAsync();

    /// <summary>Help > Get the Browser Extension… and the bar's Get it: the three browsers, each with its link.</summary>
    private async Task ShowGetExtensionAsync()
    {
        // The status is this PC's: another server's extensions pair with it over the network, which this window can't see.
        HostStatusDto? status = null;
        if (!_connection.IsRemote)
        {
            await RefreshSettingsAsync();
            status = _status;
        }

        var body = new StackPanel { Spacing = 12, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = ExtensionPromoRules.SectionHint, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(ExtensionLinksPanel(status));
        if (_connection.IsRemote)
            body.Children.Add(Wrapped("To send downloads to this server, pair the extension with it from the extension's Options."));

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Get the browser extension",
            Content = body,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowDialogAsync(dialog);
        // What the dialog fetched may show an extension that came meanwhile.
        UpdateExtensionHint(false);
    }

    /// <summary>
    /// UI.B30: the browser lists open now (the dialog's, Options'), so an extension that reaches this PC's Host while one is
    /// open shows there at once. Only lists of this PC's status are kept; one for another server shows no status.
    /// </summary>
    private readonly List<WeakReference<StackPanel>> _extensionLinkPanels = [];

    /// <summary>
    /// One line per browser: its name and where the link goes, whether its extension has connected (this PC only), and
    /// Get it. Used by the dialog above and by Options. With this PC's status it is kept up to date while it is open.
    /// </summary>
    private StackPanel ExtensionLinksPanel(HostStatusDto? status)
    {
        var panel = new StackPanel { Spacing = 10 };
        FillExtensionLinks(panel, status);
        if (status is not null)
        {
            _extensionLinkPanels.RemoveAll(kept => !kept.TryGetTarget(out _));
            _extensionLinkPanels.Add(new WeakReference<StackPanel>(panel));
        }

        return panel;
    }

    /// <summary>UI.B30: the lists open now show this PC's status as the window has it now.</summary>
    private void RefreshExtensionLinkPanels()
    {
        if (_localStatus is not { } status) return;
        _extensionLinkPanels.RemoveAll(kept => !kept.TryGetTarget(out _));
        foreach (var kept in _extensionLinkPanels)
            if (kept.TryGetTarget(out var panel)) FillExtensionLinks(panel, status);
    }

    /// <summary>
    /// UI.B30: Options brought to the front again shows what this PC's Host says now, not what it said when it opened.
    /// Quiet when the Host can't be reached: the list keeps what it had.
    /// </summary>
    private async Task RefreshExtensionLinksFromHostAsync()
    {
        try
        {
            var latest = await _local.RefreshAsync();
            if (latest.Status is null) return;
            _localStatus = latest.Status;
            if (_connection == _local) _status = latest.Status;
            RefreshExtensionLinkPanels();
            UpdateExtensionHint(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException or InvalidDataException)
        {
            // Not connected just now.
        }
    }

    private static void FillExtensionLinks(StackPanel panel, HostStatusDto? status)
    {
        // The registration's words point at the status bar's Repair, which is Windows-only (Linux registers by itself).
        var links = ExtensionPromoRules.Links(status?.ExtensionsSeen, status?.BrowserIntegration, OperatingSystem.IsWindows());
        // Unchanged rows are left alone, so a focused Get it keeps its focus.
        var showsSeen = status?.ExtensionsSeen is not null;
        if (panel.Tag is (IReadOnlyList<ExtensionLink> shown, bool shownSeen) && shownSeen == showsSeen && shown.SequenceEqual(links)) return;
        panel.Tag = (links, showsSeen);
        panel.Children.Clear();
        foreach (var link in links)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = link.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var from = InboundUi.Secondary(link.From);
            from.TextWrapping = TextWrapping.Wrap;
            words.Children.Add(from);
            row.Children.Add(words);

            if (link.Status is { } said)
            {
                var brush = link.Connected ? "InboundStatusDoneBrush"
                    : said.StartsWith("Can't reach", StringComparison.Ordinal) ? "InboundStatusFailedBrush"
                    : "InboundStatusNeutralBrush";
                var chip = new StatusChip(said, brush, small: true);
                Grid.SetColumn(chip.Element, 1);
                row.Children.Add(chip.Element);
            }

            var get = new HyperlinkButton { Content = "Get it", NavigateUri = new Uri(link.Url), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(get, link.Url);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(get, $"Get the extension for {link.Name}");
            Grid.SetColumn(get, 2);
            row.Children.Add(get);
            panel.Children.Add(row);
        }

        if (status?.ExtensionsSeen is not null) panel.Children.Add(Wrapped(ExtensionPromoRules.EdgeAlso));
    }

    private static TextBlock Wrapped(string text)
    {
        var block = InboundUi.Secondary(text);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }
}
