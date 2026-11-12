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

        if (decision.Show && !ExtensionHintBar.IsOpen)
        {
            ExtensionHintBar.Title = ExtensionPromoRules.HintTitle;
            ExtensionHintBar.Message = ExtensionPromoRules.HintMessage;
        }

        if (decision.Show) ExtensionHintBar.IsOpen = true;
        else if (ExtensionHintBar.IsOpen) CloseExtensionHint();
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
        ExtensionHintBar.IsOpen = false;
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
    /// One line per browser: its name and where the link goes, whether its extension has connected (this PC only), and
    /// Get it. Used by the dialog above and by Options.
    /// </summary>
    private static StackPanel ExtensionLinksPanel(HostStatusDto? status)
    {
        var panel = new StackPanel { Spacing = 10 };
        // The registration's words point at the status bar's Repair, which is Windows-only (Linux registers by itself).
        var links = ExtensionPromoRules.Links(status?.ExtensionsSeen, status?.BrowserIntegration, OperatingSystem.IsWindows());
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
        return panel;
    }

    private static TextBlock Wrapped(string text)
    {
        var block = InboundUi.Secondary(text);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }
}
