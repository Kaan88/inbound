using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Stages scheduler launch, synchronization and retry options for a schedule/queue.</summary>
public sealed class SchedulerRunOptionsEditor
{
    private readonly CheckBox _startOnStartup = new() { Content = "Start this queue when Inbound starts" };
    private readonly CheckBox _timedStart = new() { Content = "Start this queue at the scheduled time" };
    private readonly CheckBox _repeatEnabled = new() { Content = "Periodically check completed files for changes" };
    private readonly NumberBox _repeatMinutes = new()
    {
        Minimum = 1,
        Maximum = 1440,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        Value = 60,
        SmallChange = 1,
    };
    private readonly CheckBox _retryEnabled = new() { Content = "Limit retries after a file fails" };
    private readonly NumberBox _retryLimit = new()
    {
        Minimum = 0,
        Maximum = 100,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        Value = 3,
        SmallChange = 1,
    };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    public UIElement Content { get; }

    public SchedulerRunOptionsEditor(ScheduleDto? current, int? retryLimit)
    {
        _startOnStartup.IsChecked = current?.StartOnHostStartup == true;
        _timedStart.IsChecked = current?.TimedStartEnabled ?? true;
        _repeatEnabled.IsChecked = current?.RepeatEveryMinutes is not null;
        if (current?.RepeatEveryMinutes is int repeat) _repeatMinutes.Value = repeat;
        _retryEnabled.IsChecked = retryLimit.HasValue;
        if (retryLimit.HasValue) _retryLimit.Value = retryLimit.Value;

        _repeatEnabled.Checked += (_, _) => UpdateEnabledState();
        _repeatEnabled.Unchecked += (_, _) => UpdateEnabledState();
        _retryEnabled.Checked += (_, _) => UpdateEnabledState();
        _retryEnabled.Unchecked += (_, _) => UpdateEnabledState();
        _timedStart.Checked += (_, _) => UpdateEnabledState();
        _timedStart.Unchecked += (_, _) => UpdateEnabledState();

        var repeatLine = new Grid { ColumnSpacing = 8 };
        repeatLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        repeatLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_repeatMinutes, 1);
        repeatLine.Children.Add(_repeatEnabled);
        repeatLine.Children.Add(_repeatMinutes);

        var retryLine = new Grid { ColumnSpacing = 8 };
        retryLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        retryLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_retryLimit, 1);
        retryLine.Children.Add(_retryEnabled);
        retryLine.Children.Add(_retryLimit);

        var panel = new StackPanel { Spacing = 10, MaxWidth = 680 };
        panel.Children.Add(new TextBlock { Text = "Queue run options", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(_startOnStartup);
        panel.Children.Add(_timedStart);
        panel.Children.Add(repeatLine);
        panel.Children.Add(new TextBlock
        {
            Text = "Checks whether finished files have changed on the server and downloads them again if so. It checks between the schedule’s start and stop times.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(retryLine);
        panel.Children.Add(new TextBlock { Text = "Uncheck the retry limit for unlimited retries. A limit of 0 disables retries; 1–100 sets the number of retries after a file failure.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_error);
        Content = new ContentControl { Content = panel, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        UpdateEnabledState();
    }

    internal string StateKey => string.Join("|", _startOnStartup.IsChecked, _timedStart.IsChecked,
        _repeatEnabled.IsChecked, _repeatMinutes.Value, _retryEnabled.IsChecked, _retryLimit.Value);

    public void SetRetryLimit(int? retryLimit)
    {
        _retryEnabled.IsChecked = retryLimit.HasValue;
        _retryLimit.Value = retryLimit ?? 3;
        UpdateEnabledState();
    }

    public bool TryGet(out bool startOnStartup, out bool timedStart, out int? repeatMinutes, out int? retryLimit, out string? error)
    {
        startOnStartup = _startOnStartup.IsChecked == true;
        timedStart = _timedStart.IsChecked == true;
        repeatMinutes = null;
        retryLimit = null;
        error = null;

        if (!startOnStartup && !timedStart)
            error = "Enable startup or timed start so this queue has a way to run.";
        else if (_repeatEnabled.IsChecked == true && !timedStart)
            error = "Periodic synchronization requires timed start.";
        else if (_repeatEnabled.IsChecked == true && !TryGetWholeNumber(_repeatMinutes, 1, 1440, out _))
            error = "Set the synchronization interval to a whole number from 1 to 1440 minutes.";
        else if (_retryEnabled.IsChecked == true && !TryGetWholeNumber(_retryLimit, 0, 100, out _))
            error = "Set the retry limit to a whole number from 0 to 100.";

        if (error is null)
        {
            if (_repeatEnabled.IsChecked == true)
            {
                TryGetWholeNumber(_repeatMinutes, 1, 1440, out var minutes);
                repeatMinutes = (int)minutes;
            }
            if (_retryEnabled.IsChecked == true)
            {
                TryGetWholeNumber(_retryLimit, 0, 100, out var retries);
                retryLimit = (int)retries;
            }
        }

        _error.Text = error ?? string.Empty;
        _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        return error is null;
    }

    private void UpdateEnabledState()
    {
        _repeatEnabled.IsEnabled = _timedStart.IsChecked == true;
        _repeatMinutes.IsEnabled = _timedStart.IsChecked == true && _repeatEnabled.IsChecked == true;
        _retryLimit.IsEnabled = _retryEnabled.IsChecked == true;
    }

    private static bool TryGetWholeNumber(NumberBox box, int minimum, int maximum, out double value)
    {
        value = box.Value;
        return double.IsFinite(value) && value == Math.Truncate(value) && value >= minimum && value <= maximum;
    }
}
