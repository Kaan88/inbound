using System.Globalization;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace AndroidTrial.MauiTk;

public partial class DownloadsPage : ContentPage
{
    private readonly DownloadsModel _model;

    public DownloadsPage()
    {
        InitializeComponent();
        _model = new DownloadsModel(a => MainThread.BeginInvokeOnMainThread(a));
        List.ItemsSource = _model.Rows;
        Loaded += (_, _) =>
        {
            if (Dynamic.Enabled) ((App)Microsoft.Maui.Controls.Application.Current!).Apply();
            if (Platform.CurrentActivity is { } act) TrialSupport.Begin(act, _model, MainActivity.Options);
        };
    }
}

/// <summary>A progress bar that animates to each new value (MAUI's ProgressTo) instead of jumping.</summary>
public sealed class AnimatedBar : ProgressBar
{
    public static readonly BindableProperty TargetProperty = BindableProperty.Create(nameof(Target), typeof(double), typeof(AnimatedBar), 0.0,
        propertyChanged: (b, o, n) => ((AnimatedBar)b).Go((double)n));

    public double Target { get => (double)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    private bool _first = true;

    private void Go(double v)
    {
        if (_first) { _first = false; Progress = v; return; }
        this.AbortAnimation("p");
        this.ProgressTo(v, 250, Easing.Linear);
    }
}

public sealed class KindColor : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var r = Microsoft.Maui.Controls.Application.Current!.Resources;
        var key = (StatusKind?)value switch { StatusKind.Done => "Done", StatusKind.Failed => "Failed", StatusKind.Waiting => "Waiting", _ => "Muted" };
        return r[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
