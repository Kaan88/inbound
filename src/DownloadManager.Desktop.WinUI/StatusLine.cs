using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>A line of status words that screen readers hear when it changes, in error colors when it reports a problem.</summary>
internal sealed class StatusLine
{
    private bool _isProblem;

    public StatusLine(string accessibleName)
    {
        AccessibleName = accessibleName;
        Text = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(Text, accessibleName);
        AutomationProperties.SetLiveSetting(Text, AutomationLiveSetting.Polite);
        Text.Loaded += (_, _) => ApplyColor();
        Text.ActualThemeChanged += (_, _) => ApplyColor();
    }

    public string AccessibleName { get; }

    public TextBlock Text { get; }

    /// <summary>Shows words that may be refreshed on their own. Screen readers hear them only when they change, so a timer can't repeat itself.</summary>
    public void Show(string text, bool isProblem = false) => Apply(text, isProblem, announceSameWords: false);

    /// <summary>Shows the outcome of something the user just did. It is announced even when the words are the same as last time.</summary>
    public void Say(string text, bool isProblem = false) => Apply(text, isProblem, announceSameWords: true);

    private void Apply(string text, bool isProblem, bool announceSameWords)
    {
        var changed = Text.Text != text;
        _isProblem = isProblem;
        Text.Text = text;
        Text.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(Text, text.Length == 0 ? AccessibleName : $"{AccessibleName}: {text}");
        ApplyColor();
        if (text.Length > 0 && (changed || announceSameWords)) LiveRegion.Announce(Text);
    }

    private void ApplyColor()
    {
        if (_isProblem) Text.Foreground = InboundTheme.Brush("InboundErrorTextBrush", Text);
        else Text.ClearValue(TextBlock.ForegroundProperty);
    }
}
