using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The favorites button beside a folder box and the free-space line under it. Favorites live in the Host's settings
/// and free space is measured by the Host, so both stay right when the files are written somewhere this window
/// can't see.
/// </summary>
internal static class FolderChooser
{
    /// <summary>Set once by the main window: how this window reaches the Host.</summary>
    public static Func<IReadOnlyList<string>, Task<FolderSpaceResult?>>? Measure { get; set; }

    public static Func<SettingsDto?>? Settings { get; set; }

    /// <summary>Saves the favorites list. Returns the error, or null once saved.</summary>
    public static Func<List<FavoriteFolderDto>, Task<string?>>? SaveFavorites { get; set; }

    /// <summary>
    /// Adds the favorites button as a new last column of <paramref name="row"/> and returns the row with the space line
    /// under it, to place where the row was.
    /// </summary>
    /// <param name="neededBytes">The download's size when known, to warn when it won't fit.</param>
    public static UIElement Attach(Grid row, TextBox box, Func<long?>? neededBytes = null) => Attach(row, box, neededBytes, out _);

    /// <param name="refresh">Measures again, for when the needed size becomes known.</param>
    public static UIElement Attach(Grid row, TextBox box, Func<long?>? neededBytes, out Action refresh)
    {
        var favorites = new Button
        {
            Content = new FontIcon { Glyph = "\uE734", FontSize = 14 },
            VerticalAlignment = VerticalAlignment.Bottom,
            MinWidth = 40,
        };
        // Review 5:W2a: Options > Save to has several in a row, so each says which box it is for.
        var favoritesName = FavoritesName(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box) is { Length: > 0 } named
            ? named : box.Header as string);
        ToolTipService.SetToolTip(favorites, "Favorite folders");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(favorites, favoritesName);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(favorites, row.ColumnDefinitions.Count - 1);
        row.Children.Add(favorites);

        var space = InboundUi.Secondary();
        space.Visibility = Visibility.Collapsed;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        favorites.Flyout = menu;
        menu.Opening += (_, _) => FillMenu(menu, box, favorites);

        var version = 0;
        async void Refresh()
        {
            var mine = ++version;
            await Task.Delay(400);
            if (mine != version) return;
            var folder = box.Text.Trim();
            if (folder.Length == 0 || Measure is null) { space.Visibility = Visibility.Collapsed; return; }
            FolderSpaceDto? measured = null;
            try { measured = (await Measure([folder]))?.Folders.FirstOrDefault(); }
            catch (Exception ex) { UiTrace.Write($"Measuring free space failed: {ex.GetType().Name}"); }
            if (mine != version) return;
            var (text, problem) = SpaceLine(measured, neededBytes?.Invoke());
            space.Text = text;
            space.Opacity = problem ? 1 : 0.72;
            if (problem) space.Foreground = InboundTheme.Brush("InboundErrorTextBrush", box);
            else space.ClearValue(TextBlock.ForegroundProperty);
            space.Visibility = Visibility.Visible;
        }

        // Review 5:A3: a long path shows its end (the folder the files go to), as the web UI's box does, and the whole
        // path as the tooltip. Only while the box isn't being typed in, so the caret stays where the user put it.
        void ShowEnd()
        {
            ToolTipService.SetToolTip(box, box.Text.Length == 0 ? null : box.Text);
            if (box.FocusState != FocusState.Unfocused) return;
            box.Select(box.Text.Length, 0);
            box.DispatcherQueue?.TryEnqueue(() =>
            {
                if (box.FocusState == FocusState.Unfocused && FindScrollViewer(box) is { } viewer)
                    viewer.ChangeView(viewer.ScrollableWidth, null, null, true);
            });
        }

        refresh = Refresh;
        box.TextChanged += (_, _) => { Refresh(); ShowEnd(); };
        box.Loaded += (_, _) => { Refresh(); ShowEnd(); };
        box.LostFocus += (_, _) => ShowEnd();
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(row);
        panel.Children.Add(space);
        return panel;
    }

    /// <summary>The favorites button's spoken name: which folder box it is for.</summary>
    public static string FavoritesName(string? boxName)
        => string.IsNullOrWhiteSpace(boxName) ? "Favorite folders" : $"Favorite folders for {boxName.Trim()}";

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>What the line under the box says, and whether it's a warning.</summary>
    public static (string Text, bool Problem) SpaceLine(FolderSpaceDto? measured, long? needed)
    {
        if (measured is null || measured.FreeBytes is not { } free) return ("Free space unknown", false);
        if (measured.InsideContainer)
            return ("This folder isn't on a mounted volume, so files saved here are lost when the container is rebuilt.", true);
        if (needed is > 0 && needed > free) return ($"Not enough space: needs {Size(needed.Value)}, {Size(free)} free", true);
        return (measured.TotalBytes is { } total ? $"{Size(free)} free of {Size(total)}" : $"{Size(free)} free", false);
    }

    public static string Size(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024, Tb = Gb * 1024;
        return bytes >= Tb ? $"{bytes / Tb:N1} TB" : bytes >= Gb ? $"{bytes / Gb:N1} GB" : bytes >= Mb ? $"{bytes / Mb:N0} MB" : $"{bytes / Kb:N0} KB";
    }

    /// <summary>The name a favorite gets by default: the folder's last part.</summary>
    public static string DefaultName(string path)
    {
        var name = Path.GetFileName(path.Trim().TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? path.Trim() : name;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim().TrimEnd('\\', '/'), b.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static void FillMenu(MenuFlyout menu, TextBox box, Button anchor)
    {
        menu.Items.Clear();
        var settings = Settings?.Invoke();
        var saved = settings?.FavoriteFolders ?? [];
        var current = box.Text.Trim();
        var spaced = new List<(MenuFlyoutItem Item, string Label, string Path)>();

        MenuFlyoutItem Pick(string label, string path)
        {
            var item = new MenuFlyoutItem { Text = label };
            ToolTipService.SetToolTip(item, path);
            item.Click += (_, _) => box.Text = path;
            spaced.Add((item, label, path));
            return item;
        }

        if (saved.Count == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = "No favorites yet", IsEnabled = false });
        foreach (var favorite in saved)
            menu.Items.Add(Pick($"{favorite.Name}  ·  {favorite.Path}", favorite.Path));

        var recent = (settings?.RecentDestinationDirectories ?? [])
            .Where(path => !saved.Any(favorite => Same(favorite.Path, path))).ToList();
        if (recent.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = "Recent", IsEnabled = false });
            foreach (var path in recent) menu.Items.Add(Pick(path, path));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var existing = saved.FirstOrDefault(favorite => Same(favorite.Path, current));
        if (existing is not null)
        {
            var remove = new MenuFlyoutItem { Text = $"Remove \"{existing.Name}\" from favorites" };
            remove.Click += async (_, _) => await SaveAsync(saved.Where(favorite => favorite != existing).ToList(), anchor);
            menu.Items.Add(remove);
        }
        else
        {
            var add = new MenuFlyoutItem { Text = "Save this folder as a favorite…", IsEnabled = current.Length > 0 && SaveFavorites is not null };
            add.Click += (_, _) => AskName(anchor, current, saved);
            menu.Items.Add(add);
        }

        _ = AddSpaceAsync(spaced);
    }

    private static async Task AddSpaceAsync(List<(MenuFlyoutItem Item, string Label, string Path)> items)
    {
        if (items.Count == 0 || Measure is null) return;
        try
        {
            var result = await Measure(items.Select(item => item.Path).ToList());
            foreach (var (item, label, path) in items)
            {
                var measured = result?.Folders.FirstOrDefault(folder => Same(folder.Path, path));
                item.Text = measured?.FreeBytes is { } free ? $"{label}  ·  {Size(free)} free" : $"{label}  ·  space unknown";
            }
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Measuring favorites failed: {ex.GetType().Name}");
        }
    }

    private static void AskName(FrameworkElement anchor, string path, List<FavoriteFolderDto> saved)
    {
        var name = new TextBox { Header = "Name", Text = DefaultName(path), Width = 260 };
        var save = new Button { Content = "Save", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        var message = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, MaxWidth = 260 };
        var panel = new StackPanel { Spacing = 8 };
        var pathText = InboundUi.Secondary(path);
        pathText.MaxWidth = 260;
        panel.Children.Add(pathText);
        panel.Children.Add(name);
        panel.Children.Add(message);
        panel.Children.Add(save);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        async Task Save()
        {
            save.IsEnabled = false;
            var list = saved.ToList();
            list.Add(new FavoriteFolderDto { Name = string.IsNullOrWhiteSpace(name.Text) ? DefaultName(path) : name.Text.Trim(), Path = path });
            var error = await SaveAsync(list, anchor);
            if (error is null) flyout.Hide();
            else { message.Text = error; message.Visibility = Visibility.Visible; save.IsEnabled = true; }
        }

        save.Click += async (_, _) => await Save();
        name.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await Save(); } };
        flyout.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        flyout.ShowAt(anchor);
    }

    private static async Task<string?> SaveAsync(List<FavoriteFolderDto> list, FrameworkElement anchor)
    {
        if (SaveFavorites is null) return "The background host is not connected.";
        try
        {
            return await SaveFavorites(list);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Saving favorites failed: {ex.GetType().Name}");
            return "Couldn't save the favorites.";
        }
    }
}
