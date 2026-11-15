using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AndroidTrial.UnoTk;

public sealed partial class MainPage : Page
{
    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer s) return s;
            if (FindScrollViewer(c) is { } r) return r;
        }

        return null;
    }

    private readonly DownloadsModel _model;

    public MainPage()
    {
        InitializeComponent();
        var q = DispatcherQueue.GetForCurrentThread();
        _model = new DownloadsModel(a => q.TryEnqueue(() => a()));
        List.ItemsSource = _model.Rows;
        _model.Rows.CollectionChanged += (_, e) =>
        {
            // A list that is at the top stays at the top when a new row is inserted above it.
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && _model.Rows.Count > 1)
            {
                var sv = FindScrollViewer(List);
                if (sv is null || sv.VerticalOffset < 1) q.TryEnqueue(() => List.ScrollIntoView(_model.Rows[0]));
            }
        };
        if (Droid.MainActivity.Current is { } act) TrialSupport.Begin(act, _model, Droid.MainActivity.Options);
    }
}

/// <summary>A ProgressBar that animates to each new value (DoubleAnimation on Value) instead of jumping.</summary>
public sealed partial class AnimatedBar : ProgressBar
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(double), typeof(AnimatedBar),
        new PropertyMetadata(0.0, (d, e) => ((AnimatedBar)d).Go((double)e.NewValue)));

    public double Target { get => (double)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    private bool _first = true;
    private Storyboard? _sb;

    private void Go(double v)
    {
        if (_first) { _first = false; Value = v; return; }
        _sb?.Stop();
        var a = new DoubleAnimation { To = v, Duration = new Duration(TimeSpan.FromMilliseconds(250)), EnableDependentAnimation = true };
        Storyboard.SetTarget(a, this);
        Storyboard.SetTargetProperty(a, "Value");
        _sb = new Storyboard();
        _sb.Children.Add(a);
        _sb.Begin();
    }
}

public sealed class KindBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (parameter as string == "vis") return (bool)value ? Visibility.Visible : Visibility.Collapsed;
        var key = (StatusKind)value switch { StatusKind.Done => "Done", StatusKind.Failed => "Failed", StatusKind.Waiting => "Waiting", _ => "Muted" };
        return (Brush)Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
