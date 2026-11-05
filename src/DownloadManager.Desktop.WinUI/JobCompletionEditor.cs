using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Stages per-job completion behavior without mutating the supplied options.</summary>
public sealed class JobCompletionEditor
{
    private readonly Window _owner;
    private readonly bool _systemActionsEnabled;
    private readonly ComboBox _programMode = new() { Header = "When this download completes", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _programPath = new() { MinWidth = 0, PlaceholderText = "Select an executable" };
    private readonly Button _browse = new() { Content = "Browse…" };
    private readonly CheckBox _openFile = new() { Content = "Open the downloaded file when complete" };
    private readonly ComboBox _systemAction = new() { Header = "System action", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _force = new() { Content = "Force the system action" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _systemActionNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 };

    public UIElement Content { get; }

    public JobCompletionEditor(JobCompletionOptionsDto current, Window owner, bool systemActionsEnabled)
    {
        ArgumentNullException.ThrowIfNull(current);
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _systemActionsEnabled = systemActionsEnabled;

        _programMode.Items.Add(new ComboBoxItem { Content = "Use global program" });
        _programMode.Items.Add(new ComboBoxItem { Content = "No completion program" });
        _programMode.Items.Add(new ComboBoxItem { Content = "Run a program" });
        _programMode.SelectedIndex = current.ProgramPath is null ? 0 : current.ProgramPath.Length == 0 ? 1 : 2;
        _programPath.Text = current.ProgramPath ?? string.Empty;

        _systemAction.Items.Add(new ComboBoxItem { Content = "No system action", Tag = "none" });
        _systemAction.Items.Add(new ComboBoxItem { Content = "Exit application", Tag = "exit" });
        _systemAction.Items.Add(new ComboBoxItem { Content = "Shut down", Tag = "shutdown" });
        _systemAction.Items.Add(new ComboBoxItem { Content = "Restart", Tag = "restart" });
        _systemAction.Items.Add(new ComboBoxItem { Content = "Log off", Tag = "logoff" });
        var action = string.IsNullOrWhiteSpace(current.SystemAction) ? "none" : current.SystemAction;
        _systemAction.SelectedItem = _systemAction.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, action, StringComparison.OrdinalIgnoreCase));
        _openFile.IsChecked = current.OpenFile;
        _force.IsChecked = current.ForceSystemAction;
        _systemActionNote.Text = systemActionsEnabled
            ? "A system action runs only when no other downloads are unfinished."
            : "Enable system completion actions in Scheduler settings to use this option.";

        _systemAction.SelectionChanged += (_, _) => UpdateForceChoice();
        UpdateForceChoice();
        _programMode.SelectionChanged += (_, _) => UpdateProgramMode();
        _browse.Click += async (_, _) => await BrowseAsync();

        var programLine = new Grid { ColumnSpacing = 8 };
        programLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        programLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_programPath, 0);
        Grid.SetColumn(_browse, 1);
        programLine.Children.Add(_programPath);
        programLine.Children.Add(_browse);

        // Plain fields with the same header style as the rest of the properties dialog.
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_programMode);
        panel.Children.Add(programLine);
        panel.Children.Add(_openFile);
        panel.Children.Add(_systemAction);
        panel.Children.Add(_systemActionNote);
        panel.Children.Add(_force);
        panel.Children.Add(_error);
        Content = new ContentControl { Content = panel, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        UpdateProgramMode();
    }

    public bool TryGetOptions(out JobCompletionOptionsDto options, out string? error)
    {
        error = null;
        options = new JobCompletionOptionsDto
        {
            ProgramPath = _programMode.SelectedIndex switch
            {
                0 => null,
                1 => string.Empty,
                2 => _programPath.Text.Trim(),
                _ => null,
            },
            OpenFile = _openFile.IsChecked == true,
            SystemAction = (_systemAction.SelectedItem as ComboBoxItem)?.Tag as string ?? "none",
            ForceSystemAction = _force.IsChecked == true,
        };

        if (_programMode.SelectedIndex < 0)
            error = "Choose how to handle the program after completion.";
        else if (_programMode.SelectedIndex == 2 && !IsAbsoluteExecutable(options.ProgramPath))
            error = "Choose an absolute path to an existing .exe file.";
        else if (options.SystemAction != "none" && !_systemActionsEnabled)
            error = "Enable system completion actions in Scheduler settings before choosing a system action.";
        else if (!new[] { "none", "exit", "shutdown", "restart", "logoff" }.Contains(options.SystemAction, StringComparer.Ordinal))
            error = "Choose a valid system action.";

        _error.Text = error ?? string.Empty;
        _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        return error is null;
    }

    private void UpdateForceChoice()
    {
        _force.IsEnabled = (_systemAction.SelectedItem as ComboBoxItem)?.Tag as string is "shutdown" or "restart" or "logoff";
    }

    private void UpdateProgramMode()
    {
        var enabled = _programMode.SelectedIndex == 2;
        _programPath.IsEnabled = enabled;
        _browse.IsEnabled = enabled;
    }

    private async Task BrowseAsync()
    {
        _browse.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            _programPath.Text = file.Path;
            _programMode.SelectedIndex = 2;
            _error.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _error.Text = $"Could not select a program: {ex.Message}";
            _error.Visibility = Visibility.Visible;
        }
        finally
        {
            _browse.IsEnabled = _programMode.SelectedIndex == 2;
        }
    }

    private static bool IsAbsoluteExecutable(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
