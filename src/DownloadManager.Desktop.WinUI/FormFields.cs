using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Picker-based form fields that keep the string formats the scheduler already stores ("HH:mm",
/// "Mon,Tue", "yyyy-MM-dd"), so editors swap a free-text box for a proper picker without changing
/// validation or persistence.
/// </summary>
internal sealed class TimeField : UserControl
{
    private readonly TimePicker _picker = new() { ClockIdentifier = "24HourClock", MinuteIncrement = 1, MinWidth = 0 };

    public TimeField(string header, bool optional)
    {
        var panel = new StackPanel { Spacing = 4 };
        var label = new TextBlock { Text = header };
        panel.Children.Add(label);
        IsEnabledChanged += (_, _) => label.Opacity = IsEnabled ? 1 : 0.45;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(_picker);
        if (optional)
        {
            var clear = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8, 7, 8, 7),
                VerticalAlignment = VerticalAlignment.Stretch };
            ToolTipService.SetToolTip(clear, "No stop time");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clear, "Clear " + header);
            clear.Click += (_, _) => _picker.SelectedTime = null;
            row.Children.Add(clear);
        }
        panel.Children.Add(row);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_picker, header);
        Content = panel;
    }

    public string Text
    {
        get => _picker.SelectedTime is { } time ? $"{time.Hours:00}:{time.Minutes:00}" : string.Empty;
        set => _picker.SelectedTime = TimeOnly.TryParseExact(value?.Trim(), "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed.ToTimeSpan() : null;
    }
}

internal sealed class DaysField : UserControl
{
    private static readonly string[] Names = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private readonly ToggleButton[] _days;

    public DaysField(string header)
    {
        var panel = new StackPanel { Spacing = 4 };
        var label = new TextBlock { Text = header };
        panel.Children.Add(label);
        IsEnabledChanged += (_, _) => label.Opacity = IsEnabled ? 1 : 0.45;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _days = Names.Select(name => new ToggleButton { Content = name, MinWidth = 46, Padding = new Thickness(6, 5, 6, 6) }).ToArray();
        foreach (var day in _days) row.Children.Add(day);
        var every = new HyperlinkButton { Content = "Every day", Padding = new Thickness(8, 5, 4, 6) };
        every.Click += (_, _) => { foreach (var day in _days) day.IsChecked = true; };
        row.Children.Add(every);
        panel.Children.Add(row);
        Content = panel;
    }

    /// <summary>Comma list of checked day names; an empty selection means every day, as in the host.</summary>
    public string Text
    {
        get
        {
            var picked = Names.Where((_, index) => _days[index].IsChecked == true).ToArray();
            return picked.Length == 0 ? string.Join(",", Names) : string.Join(",", picked);
        }
        set
        {
            var tokens = (value ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var all = tokens.Length == 0;
            for (var index = 0; index < Names.Length; index++)
                _days[index].IsChecked = all || tokens.Any(token => token.Length >= 3 &&
                    token[..3].Equals(Names[index], StringComparison.OrdinalIgnoreCase));
        }
    }
}

internal sealed class DateField : UserControl
{
    private readonly CalendarDatePicker _picker;

    public DateField(string header, string placeholder)
    {
        _picker = new CalendarDatePicker { Header = header, PlaceholderText = placeholder, DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}",
            IsTodayHighlighted = true, MinWidth = 180 };
        Content = _picker;
    }

    public string Text
    {
        get => _picker.Date is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
        set => _picker.Date = DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? new DateTimeOffset(parsed.ToDateTime(TimeOnly.MinValue)) : null;
    }
}
