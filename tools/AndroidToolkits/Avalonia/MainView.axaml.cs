using System.Globalization;
using Avalonia;
using Application = Avalonia.Application;
using Avalonia.Controls;
using ProgressBar = Avalonia.Controls.ProgressBar;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;

namespace AndroidTrial.AvaloniaTk;

public partial class MainView : UserControl
{
    private readonly DownloadsModel _model;

    public MainView()
    {
        InitializeComponent();
        _model = new DownloadsModel(a => Dispatcher.UIThread.Post(a));
        List.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<JobRow>((_, _) => BuildRow());
        List.ItemsSource = _model.Rows;
        _model.Rows.CollectionChanged += (_, e) =>
        {
            // A list that is at the top stays at the top when a new row is inserted above (as the other toolkits' lists do).
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && List.Scroll is ScrollViewer sv && sv.Offset.Y < 1)
                Dispatcher.UIThread.Post(() => List.ScrollIntoView(0), DispatcherPriority.Background);
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                // Edge to edge: draw under the bars and pad by the safe area.
                if (top.InsetsManager is { } im)
                {
                    im.DisplayEdgeToEdgePreference = false; // true gave a doubled top/bottom offset in this trial (see report)
                }
            }

            if (MainActivity.Current is { } act) TrialSupport.Begin(act, _model, MainActivity.Options);
        };
    }
}

public partial class MainView
{
    // Built in code: the XAML compiler could not use this assembly's own types (JobRow, a converter) in a DataTemplate or resource.
    private static Control BuildRow()
    {
        var dyn = (string k) => new DynamicResourceExtension(k);
        var name = new TextBlock { FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Name"));
        name.Bind(TextBlock.ForegroundProperty, dyn("Text"));
        var sub = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Subtitle"));
        sub.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding("Kind") { Converter = new KindBrush() });
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 8, 0, 0) };
        bar.Classes.Add("row");
        bar.Bind(ProgressBar.ValueProperty, new Avalonia.Data.Binding("Progress"));
        bar.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding("ShowBar"));
        bar.Bind(ProgressBar.IsIndeterminateProperty, new Avalonia.Data.Binding("Indeterminate"));
        bar.Bind(ProgressBar.ForegroundProperty, dyn("Accent"));
        bar.Bind(ProgressBar.BackgroundProperty, dyn("Track"));
        var card = new Border { Margin = new Thickness(4, 6), Padding = new Thickness(16, 14), CornerRadius = new CornerRadius(20), Child = new StackPanel { Spacing = 2, Children = { name, sub, bar } } };
        card.Bind(Border.BackgroundProperty, dyn("Card"));
        return card;
    }
}

public sealed class KindBrush : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (StatusKind?)value switch { StatusKind.Done => "Done", StatusKind.Failed => "Failed", StatusKind.Waiting => "Waiting", _ => "Muted" };
        return Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var b) ? b : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
