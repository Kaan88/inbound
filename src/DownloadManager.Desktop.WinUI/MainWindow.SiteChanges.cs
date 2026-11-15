using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.5: New folder, Rename and Delete on server in the Sites view. Each is one Host command; the folder is listed again
/// afterwards. Delete always asks, with what the server counted.
/// </summary>
public sealed partial class MainWindow
{
    private bool _siteChangeRunning;

    private void WireSiteChanges(SitesPanel panel)
    {
        panel.RenameRequested += () => SitesRenameButton_Click(this, new RoutedEventArgs());
        panel.DeleteRequested += () => SitesDeleteButton_Click(this, new RoutedEventArgs());
    }

    private async void SitesNewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_siteChangeRunning || _sitesPanel is not { Site: { } site } panel) return;
        var name = await AskNameAsync("New folder", $"In {panel.Path} on {site.Name}", SiteChangeRules.NewFolderName(panel.ShownNames), "Make", selectLength: null);
        if (name is null) return;
        await RunSiteChangeAsync(async () =>
        {
            var result = await _remote.MakeSiteFolderAsync(new SiteMakeFolderRequest { Id = site.Id, Path = panel.Path, Name = name });
            if (!result.Ok) await ShowSitesMessageAsync("New folder", result.Message ?? "The folder was not made.");
            panel.Reload(result.Ok ? name : null);
        });
    }

    private async void SitesRenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_siteChangeRunning || _sitesPanel is not { Site: { } site } panel || panel.SelectedNames is not [var item]) return;
        var name = await AskNameAsync(item.IsFolder ? "Rename folder" : "Rename", $"In {panel.Path} on {site.Name}", item.Name, "Rename",
            SiteChangeRules.RenameSelectionLength(item.Name, item.IsFolder));
        if (name is null || name == item.Name) return;
        await RunSiteChangeAsync(async () =>
        {
            var result = await _remote.RenameSiteItemAsync(new SiteRenameRequest { Id = site.Id, Path = panel.Path, Name = item.Name, NewName = name });
            if (!result.Ok) await ShowSitesMessageAsync("Rename", result.Message ?? "It was not renamed.");
            panel.Reload(result.Ok ? name : item.Name);
        });
    }

    private async void SitesDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_siteChangeRunning || _sitesPanel is not { Site: { } site } panel) return;
        var chosen = panel.SelectedNames;
        if (chosen.Count == 0) return;
        var request = new SiteChangeRequest
        {
            Id = site.Id,
            Path = panel.Path,
            Items = [.. chosen.Select(n => new SiteChangeItemDto { Name = n.Name, IsFolder = n.IsFolder })],
        };
        await RunSiteChangeAsync(async () =>
        {
            SetSitesStatus("Counting what would be deleted…");
            var count = await _remote.CountSiteItemsAsync(request);
            SetSitesStatus(null);
            if (!count.Ok)
            {
                await ShowSitesMessageAsync("Delete on server", count.Message ?? "What would be deleted could not be counted.");
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Delete on server",
                Content = new TextBlock { Text = SiteChangeRules.DeleteQuestion(chosen, count, site.Name), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                // Delete never goes by a stray Enter.
                DefaultButton = ContentDialogButton.Close,
            };
            if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary) return;

            SetSitesStatus("Deleting on the server…");
            var result = await _remote.DeleteSiteItemsAsync(request);
            SetSitesStatus(null);
            if (SiteChangeRules.DeleteFailures(result) is { } failures) await ShowSitesMessageAsync("Delete on server", failures);
            panel.Reload();
        });
    }

    private async Task RunSiteChangeAsync(Func<Task> change)
    {
        _siteChangeRunning = true;
        UpdateSitesToolbar();
        try
        {
            await change();
        }
        finally
        {
            _siteChangeRunning = false;
            SetSitesStatus(null);
            UpdateSitesToolbar();
        }
    }

    /// <summary>A name box with its own check: the primary button stays off while the name can't be used, and says why.</summary>
    private async Task<string?> AskNameAsync(string title, string where, string start, string primary, int? selectLength)
    {
        var box = new TextBox { Header = "Name", Text = start, MaxLength = SiteNameRules.MaxLength };
        var problem = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InboundIconDangerBrush"] };
        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = where, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(problem);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        void Check()
        {
            var text = SiteNameRules.Problem(box.Text);
            problem.Text = box.Text.Length == 0 ? "" : text ?? "";
            dialog.IsPrimaryButtonEnabled = text is null;
        }

        box.TextChanged += (_, _) => Check();
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.Select(0, selectLength ?? box.Text.Length);
        };
        Check();
        return await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary && SiteNameRules.Problem(box.Text) is null ? box.Text : null;
    }

    private void SetSitesStatus(string? text)
    {
        if (text is null) UpdateStatusText();
        else StatusText.Text = text;
    }
}
