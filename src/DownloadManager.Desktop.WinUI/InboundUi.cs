using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R31: Progress Details' design language for windows built in code: cards and framed lists (radius 8 with a
/// thin edge), tiles with a smaller grey unit, a header with an icon tile and a state chip, status chips, empty
/// states with a glyph, and labelled icon buttons. Colors come from the App.xaml tokens and follow the theme.
/// </summary>
internal static class InboundUi
{
    public static Style? Style(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Style style ? style : null;

    /// <summary>Calls <paramref name="apply"/> with the token's brush now and whenever the element's theme changes.</summary>
    public static void Follow(FrameworkElement element, string key, Action<Brush> apply)
    {
        apply(InboundTheme.Brush(key, element));
        element.Loaded += (_, _) => apply(InboundTheme.Brush(key, element));
        element.ActualThemeChanged += (_, _) => apply(InboundTheme.Brush(key, element));
    }

    /// <summary>A translucent card with a 1px edge, radius 8.</summary>
    public static Border Card(UIElement? child = null, Thickness? padding = null)
    {
        var card = new Border { Style = Style("InboundCardStyle"), Child = child };
        if (padding is { } value) card.Padding = value;
        return card;
    }

    /// <summary>A list framed as a card: opaque reading surface inside a radius-8 edge.</summary>
    public static Border ListCard(UIElement? child = null)
    {
        if (child is Control control) control.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        return new Border { Style = Style("InboundListCardStyle"), Child = child };
    }

    public static TextBlock PageTitle(string text) => new() { Text = text, Style = Style("InboundPageTitleStyle") };

    public static TextBlock SectionTitle(string text) => new() { Text = text, Style = Style("InboundSectionTitleStyle") };

    public static TextBlock Secondary(string text = "") => new() { Text = text, Style = Style("InboundSecondaryTextStyle") };

    /// <summary>An outline glyph over a line of secondary text, centred, for a list or window with nothing in it.</summary>
    public static StackPanel EmptyState(string glyph, string text, out TextBlock line)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(16) };
        var icon = new FontIcon { Glyph = glyph, FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center };
        Follow(icon, "InboundSecondaryTextBrush", brush => icon.Foreground = brush);
        line = Secondary(text);
        line.TextAlignment = TextAlignment.Center;
        line.HorizontalAlignment = HorizontalAlignment.Center;
        line.FontSize = 13;
        AutomationProperties.SetName(panel, text);
        panel.Children.Add(icon);
        panel.Children.Add(line);
        return panel;
    }

    public static StackPanel EmptyState(string glyph, string text) => EmptyState(glyph, text, out _);

    /// <summary>A button with an outline glyph before its label, optionally the accent button.</summary>
    public static Button IconButton(string glyph, string text, string? iconBrush = null, bool accent = false)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 14 };
        var label = new TextBlock { Text = text };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(icon);
        content.Children.Add(label);
        var button = new Button { Content = content, MinWidth = 96 };
        if (accent && Style("AccentButtonStyle") is { } style) button.Style = style;
        if (iconBrush is not null && !accent) Follow(icon, iconBrush, brush => icon.Foreground = brush);
        AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>Delete as Progress Details has it: a labelled button with a red trash glyph.</summary>
    public static Button DeleteButton(string text = "Delete") => IconButton("", text, "InboundIconDangerBrush");

    /// <summary>The state brush for a job state, as the list colors its status text.</summary>
    public static string StateBrush(string? state) => state switch
    {
        "completed" => "InboundStatusDoneBrush",
        "failed" => "InboundStatusFailedBrush",
        "paused" or "queued" or "retryWaiting" or "retrying" => "InboundStatusWaitingBrush",
        "downloading" or "connecting" or "verifying" or "running" => "InboundStatusActiveBrush",
        _ => "InboundStatusNeutralBrush",
    };
}

/// <summary>Progress Details' state chip: a rounded border in the status color around the word.</summary>
internal sealed class StatusChip
{
    private string _brushKey = "InboundStatusNeutralBrush";

    /// <summary>The chip is a live region: screen readers hear its words when they change.</summary>
    public bool Announce
    {
        get => _announce;
        set
        {
            _announce = value;
            AutomationProperties.SetLiveSetting(Element, value ? Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite : Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Off);
        }
    }

    private bool _announce;

    public Border Element { get; }
    public TextBlock Text { get; } = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    public StatusChip(string text = "", string brushKey = "InboundStatusNeutralBrush", bool small = false)
    {
        if (small)
        {
            Text.FontSize = 11;
            Text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        }
        Element = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = small ? new Thickness(8, 0, 8, 1) : new Thickness(10, 2, 10, 3),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Text,
        };
        InboundUi.Follow(Element, "InboundCardBrush", brush => Element.Background = brush);
        Element.Loaded += (_, _) => Apply();
        Element.ActualThemeChanged += (_, _) => Apply();
        Set(text, brushKey);
    }

    public void Set(string text, string brushKey)
    {
        var changed = Text.Text != text;
        Text.Text = text;
        _brushKey = brushKey;
        Element.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(Element, text);
        Apply();
        if (_announce && changed && text.Length > 0) LiveRegion.Announce(Element);
    }

    private void Apply()
    {
        var brush = InboundTheme.Brush(_brushKey, Element);
        Text.Foreground = brush;
        Element.BorderBrush = brush;
    }
}

/// <summary>Progress Details' tile: a 12px grey label over an 18 SemiBold value with its unit in a smaller grey run.</summary>
internal sealed class InfoTile
{
    public Border Element { get; }
    public TextBlock Label { get; } = new() { FontSize = 12 };
    public Run Value { get; } = new();
    public Run Unit { get; } = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Normal };

    public InfoTile(string label = "")
    {
        Label.Text = label;
        var value = new TextBlock { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        value.Inlines.Add(Value);
        value.Inlines.Add(Unit);
        var stack = new StackPanel();
        stack.Children.Add(Label);
        stack.Children.Add(value);
        Element = new Border { Style = InboundUi.Style("InboundTileStyle"), Child = stack };
        InboundUi.Follow(Element, "InboundSecondaryTextBrush", brush => { Label.Foreground = brush; Unit.Foreground = brush; });
    }

    public void Set(string label, string value, string unit = "")
    {
        Label.Text = label;
        Value.Text = value;
        Unit.Text = unit.Length == 0 ? string.Empty : " " + unit;
        AutomationProperties.SetName(Element, $"{label}: {value} {unit}".TrimEnd());
    }

    /// <summary>A size, with its unit in the small grey run.</summary>
    public void SetBytes(string label, long bytes, string suffix = "")
    {
        var (number, unit) = UiFormat.Split(bytes);
        Set(label, number, unit + suffix);
    }
}

/// <summary>
/// Progress Details' header for a window about one item: a glyph on a 40px tile, the name at 15 SemiBold, a grey
/// subline, and a state chip on the right.
/// </summary>
internal sealed class ItemHeader
{
    public Grid Element { get; } = new() { ColumnSpacing = 12 };
    public FontIcon Glyph { get; }
    public TextBlock Title { get; } = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
    public TextBlock Subline { get; } = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    public StatusChip Chip { get; } = new();

    public ItemHeader(string glyph)
    {
        Element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        Element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Element.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Glyph = new FontIcon { Glyph = glyph, FontSize = 20 };
        var tile = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Child = Glyph,
        };
        InboundUi.Follow(tile, "InboundCardBrush", brush => tile.Background = brush);
        InboundUi.Follow(tile, "InboundCardStrokeBrush", brush => tile.BorderBrush = brush);
        InboundUi.Follow(Subline, "InboundSecondaryTextBrush", brush => Subline.Foreground = brush);
        Element.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(Title);
        text.Children.Add(Subline);
        Grid.SetColumn(text, 1);
        Element.Children.Add(text);
        Grid.SetColumn(Chip.Element, 2);
        Element.Children.Add(Chip.Element);
    }
}
