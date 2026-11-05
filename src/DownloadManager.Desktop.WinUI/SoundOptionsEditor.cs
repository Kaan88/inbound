using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Stages per-event sound choices locally until TryApplyTo is called.</summary>
public sealed class SoundOptionsEditor
{
    public const string SystemSound = "@system";
    private const long MaximumWaveBytes = 10 * 1024 * 1024;

    private static readonly (string Key, string Label)[] Events =
    [
        ("completed", "Download completed"),
        ("failed", "Download failed"),
        ("queueFinished", "Queue finished"),
    ];

    private readonly Window _owner;
    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);

    public UIElement Content { get; }

    public SoundOptionsEditor(IReadOnlyDictionary<string, string> eventSoundFiles, Window owner)
    {
        ArgumentNullException.ThrowIfNull(eventSoundFiles);
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Choose a system sound, disable a sound, or select a local WAV file for each event.",
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var (key, label) in Events)
        {
            eventSoundFiles.TryGetValue(key, out var current);
            var row = CreateRow(key, label, current);
            _rows.Add(key, row);
            panel.Children.Add(row.Container);
        }

        Content = panel;
    }

    /// <summary>Validates edited choices and applies them to the supplied settings dictionary.</summary>
    public bool TryApplyTo(IDictionary<string, string> eventSoundFiles, out string? error)
    {
        ArgumentNullException.ThrowIfNull(eventSoundFiles);
        error = null;
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, row) in _rows)
        {
            if (!row.IsDirty)
            {
                continue;
            }

            switch (row.Mode.SelectedIndex)
            {
                case 0:
                    changes[key] = SystemSound;
                    break;
                case 1:
                    changes[key] = string.Empty;
                    break;
                case 2:
                    var path = row.Path.Text.Trim();
                    if (!TryNormalizeWaveFile(path, out var normalizedPath))
                    {
                        error = $"Choose an existing local WAV file up to 10 MB for {row.Label.ToLowerInvariant()}.";
                        return false;
                    }

                    changes[key] = normalizedPath;
                    break;
                default:
                    error = $"Choose a sound option for {row.Label.ToLowerInvariant()}.";
                    return false;
            }
        }

        foreach (var (key, value) in changes)
        {
            eventSoundFiles[key] = value;
        }

        return true;
    }

    private Row CreateRow(string key, string label, string? current)
    {
        var mode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add(new ComboBoxItem { Content = "System sound" });
        mode.Items.Add(new ComboBoxItem { Content = "Disabled" });
        mode.Items.Add(new ComboBoxItem { Content = "WAV file" });

        var path = new TextBox { PlaceholderText = "Select a WAV file", MinWidth = 0 };
        var pickerError = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var browse = new Button { Content = "Browse…" };
        var row = new Row(key, label, mode, path, browse);

        if (current == string.Empty || current is null && key != "completed")
        {
            mode.SelectedIndex = 1;
        }
        else if (string.IsNullOrWhiteSpace(current) ||
                 string.Equals(current, SystemSound, StringComparison.OrdinalIgnoreCase))
        {
            // Keep an absent key absent unless the user changes its choice. This
            // preserves legacy policy such as PlayCompletionSound for completion.
            mode.SelectedIndex = 0;
        }
        else
        {
            mode.SelectedIndex = 2;
            path.Text = current;
        }

        void UpdateMode()
        {
            var isFile = mode.SelectedIndex == 2;
            path.IsEnabled = isFile;
            browse.IsEnabled = isFile;
            row.IsDirty = true;
        }

        mode.SelectionChanged += (_, _) => UpdateMode();
        path.TextChanged += (_, _) => row.IsDirty = true;
        browse.Click += async (_, _) =>
        {
            browse.IsEnabled = false;
            pickerError.Visibility = Visibility.Collapsed;
            try { await PickFileAsync(row); }
            catch (Exception ex)
            {
                pickerError.Text = $"Could not select a sound: {ex.Message}";
                pickerError.Visibility = Visibility.Visible;
            }
            finally { browse.IsEnabled = mode.SelectedIndex == 2; }
        };
        UpdateModeInitial(mode, path, browse);

        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(path, 0);
        Grid.SetColumn(browse, 1);
        line.Children.Add(path);
        line.Children.Add(browse);

        var container = new StackPanel { Spacing = 4 };
        container.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        container.Children.Add(mode);
        container.Children.Add(line);
        container.Children.Add(pickerError);
        row.Container = container;
        return row;
    }

    private async Task PickFileAsync(Row row)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".wav");
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        row.Mode.SelectedIndex = 2;
        row.Path.Text = file.Path;
        row.IsDirty = true;
    }

    private static bool TryNormalizeWaveFile(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(path) ||
                !string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path) || new FileInfo(path).Length is <= 0 or > MaximumWaveBytes)
            {
                return false;
            }

            normalizedPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void UpdateModeInitial(ComboBox mode, TextBox path, Button browse)
    {
        var isFile = mode.SelectedIndex == 2;
        path.IsEnabled = isFile;
        browse.IsEnabled = isFile;
    }

    private sealed class Row(string key, string label, ComboBox mode, TextBox path, Button browse)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public ComboBox Mode { get; } = mode;
        public TextBox Path { get; } = path;
        public Button Browse { get; } = browse;
        public StackPanel Container { get; set; } = null!;
        public bool IsDirty { get; set; }
    }
}
