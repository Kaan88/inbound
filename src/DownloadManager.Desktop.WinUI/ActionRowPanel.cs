using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T13: a window's row of buttons at the bottom right. It is one line while the buttons fit and wraps onto lines above
/// when they don't (<see cref="WindowSizeRules.LineStarts"/>), so no button is ever cut off by a narrow window or a large
/// display scaling. Each line is right-aligned, and the last buttons (the window's own action and Close) stay together on
/// the bottom line. It needs a width to wrap in: put it in a star column, not an Auto one.
/// </summary>
internal sealed partial class ActionRowPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ActionRowPanel), new PropertyMetadata(8d, (sender, _) => ((ActionRowPanel)sender).InvalidateMeasure()));

    /// <summary>Between buttons and between lines.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var widths = new List<double>(Children.Count);
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            widths.Add(child.Visibility == Visibility.Visible ? child.DesiredSize.Width : 0);
        }

        var room = double.IsFinite(availableSize.Width) ? availableSize.Width : WindowSizeRules.OneLineWidth(widths, Spacing);
        var (width, height) = Lines(widths, room, (line, _, lineHeight) => { });
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = Children.Select(child => child.Visibility == Visibility.Visible ? child.DesiredSize.Width : 0).ToList();
        var top = 0d;
        Lines(widths, finalSize.Width, (line, lineWidth, lineHeight) =>
        {
            var x = Math.Max(0, finalSize.Width - lineWidth);
            foreach (var at in line)
            {
                var child = Children[at];
                if (widths[at] <= 0)
                {
                    child.Arrange(new Rect(0, 0, 0, 0));
                    continue;
                }

                child.Arrange(new Rect(x, top + (lineHeight - child.DesiredSize.Height) / 2, widths[at], child.DesiredSize.Height));
                x += widths[at] + Spacing;
            }

            top += lineHeight + Spacing;
        });
        return finalSize;
    }

    /// <summary>Walks the lines, top to bottom; returns the widest line and the height of them all.</summary>
    private (double Width, double Height) Lines(List<double> widths, double room, Action<List<int>, double, double> each)
    {
        var starts = WindowSizeRules.LineStarts(widths, room, Spacing);
        var widest = 0d;
        var height = 0d;
        for (var line = 0; line < starts.Count; line++)
        {
            var end = line + 1 < starts.Count ? starts[line + 1] : widths.Count;
            var members = Enumerable.Range(starts[line], end - starts[line]).ToList();
            var lineWidth = WindowSizeRules.OneLineWidth(members.Select(at => widths[at]).ToList(), Spacing);
            var lineHeight = members.Where(at => widths[at] > 0).Select(at => Children[at].DesiredSize.Height).DefaultIfEmpty(0).Max();
            each(members, lineWidth, lineHeight);
            widest = Math.Max(widest, lineWidth);
            height += lineHeight + (line > 0 ? Spacing : 0);
        }

        return (widest, height);
    }
}
