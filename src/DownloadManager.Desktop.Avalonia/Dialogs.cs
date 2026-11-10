using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DownloadManager.Contracts;

namespace DownloadManager.AvaloniaUi;

/// <summary>A small modal window in the ContentDialog's layout: title, content, buttons on the right.</summary>
internal class DialogWindow : Window
{
    protected readonly StackPanel Body = new() { Spacing = 10 };
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };

    protected DialogWindow(string title, double width)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Noto Sans, Cantarell, sans-serif");
        FontSize = 13;
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        Background = Brushes.Transparent;
        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold };
        var content = new StackPanel { Spacing = 18, Margin = new Thickness(24, 20, 24, 0) };
        content.Children.Add(heading);
        content.Children.Add(Body);
        var footer = new Border { Padding = new Thickness(24, 16), Margin = new Thickness(0, 22, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0), Child = Buttons };
        footer.Bind(Border.BackgroundProperty, this.GetResourceObservable("InboundCardBrush"));
        footer.Bind(Border.BorderBrushProperty, this.GetResourceObservable("InboundCardStrokeBrush"));
        var root = new StackPanel();
        root.Children.Add(content);
        root.Children.Add(footer);
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };
        Opened += (_, _) =>
        {
            // Solid when the platform gave no backdrop.
            if (ActualTransparencyLevel == WindowTransparencyLevel.None) Bind(BackgroundProperty, this.GetResourceObservable("InboundChromeBrush"));
        };
    }

    protected Button AddButton(string text, bool primary, Action click)
    {
        var button = new Button { Content = text, MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) { button.Classes.Add("accent"); button.IsDefault = true; }
        button.Click += (_, _) => click();
        Buttons.Children.Add(button);
        return button;
    }
}

/// <summary>Add URL: the address, where it goes, and an optional name. Start now or later.</summary>
internal sealed class AddUrlDialog : DialogWindow
{
    public AddUrlDialog(string defaultFolder) : base("Add URL", 560)
    {
        var url = new TextBox { PlaceholderText = "https://…", };
        var folder = new TextBox { Text = defaultFolder };
        var name = new TextBox { PlaceholderText = "Taken from the server when empty" };
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Save downloads to" });
            if (picked is [{ } first] && first.TryGetLocalPath() is { } path) folder.Text = path;
        };
        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        folderRow.Children.Add(folder);
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(browse);

        Body.Children.Add(Field("Address", url));
        Body.Children.Add(Field("Save to", folderRow));
        Body.Children.Add(Field("File name", name));

        AddJobRequest Request(string mode) => new()
        {
            Url = url.Text!.Trim(),
            DestinationDirectory = string.IsNullOrWhiteSpace(folder.Text) ? null : folder.Text.Trim(),
            FileName = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim(),
            StartMode = mode,
            ConflictPolicy = "rename",
            Source = "api",
        };
        var start = AddButton("Start Download", primary: true, () => Close(Request("now")));
        var later = AddButton("Download Later", primary: false, () => Close(Request("later")));
        AddButton("Cancel", primary: false, () => Close(null));
        void Sync() => start.IsEnabled = later.IsEnabled = Uri.TryCreate(url.Text?.Trim(), UriKind.Absolute, out var address) && address.Scheme is "http" or "https" or "ftp";
        url.TextChanged += (_, _) => Sync();
        Sync();
        Opened += (_, _) => url.Focus();
    }

    private static Control Field(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        panel.Children.Add(input);
        return panel;
    }
}

/// <summary>A message, or a question with an optional check box. Null when cancelled.</summary>
internal sealed class MessageDialog : DialogWindow
{
    private MessageDialog(string title, string text) : base(title, 460)
        => Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });

    public static Task ShowAsync(Window owner, string title, string text)
    {
        var dialog = new MessageDialog(title, text);
        dialog.AddButton("OK", primary: true, () => dialog.Close(null));
        return dialog.ShowDialog<object?>(owner);
    }

    /// <returns>Null for Cancel, otherwise whether the check box was ticked.</returns>
    public static Task<bool?> ConfirmAsync(Window owner, string title, string text, string action, string? checkBox)
    {
        var dialog = new MessageDialog(title, text);
        var box = checkBox is null ? null : new CheckBox { Content = checkBox };
        if (box is not null) dialog.Body.Children.Add(box);
        dialog.AddButton(action, primary: true, () => dialog.Close((bool?)(box?.IsChecked == true)));
        dialog.AddButton("Cancel", primary: false, () => dialog.Close(null));
        return dialog.ShowDialog<bool?>(owner);
    }
}
