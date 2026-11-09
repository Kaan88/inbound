using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using WinRT.Interop;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// For a download on another server (design D9, section 5): its name, and what its hello lets this app set there. The
/// program is a path on the server, so it is never looked for or chosen on this PC.
/// </summary>
public sealed record RemoteCompletionScope(string ServerName, bool ProgramSettings, bool CompletionActions);

/// <summary>Stages per-job completion behavior without mutating the supplied options.</summary>
public sealed class JobCompletionEditor
{
    private readonly Window _owner;
    private readonly bool _systemActionsEnabled;
    private readonly RemoteCompletionScope? _remote;
    private readonly ComboBox _programMode = new() { Header = "When this download completes", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _programPath = new() { MinWidth = 0, PlaceholderText = "Select an executable" };
    private readonly Button _browse = new() { Content = "Browse…" };
    private readonly CheckBox _openFile = new() { Content = "Open the downloaded file when complete" };
    private readonly ComboBox _systemAction = new() { Header = "System action", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _force = new() { Content = "Force the system action" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _systemActionNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 };

    public UIElement Content { get; }

    /// <param name="remote">Null for this PC's Host. For another server: no file is checked or picked here, and only what
    /// its hello allows can be changed.</param>
    public JobCompletionEditor(JobCompletionOptionsDto current, Window owner, bool systemActionsEnabled, RemoteCompletionScope? remote = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _remote = remote;
        _systemActionsEnabled = systemActionsEnabled && remote?.CompletionActions != false;

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
        _systemActionNote.Text = remote is { CompletionActions: false }
            ? $"{remote.ServerName} doesn't let this app open files or shut it down when a download finishes."
            : _systemActionsEnabled
                ? "A system action runs only when no other downloads are unfinished."
                : "Enable system completion actions in Scheduler settings to use this option.";
        if (remote is not null)
        {
            // A path on the server: there is nothing on this PC to browse for.
            _browse.Visibility = Visibility.Collapsed;
            _programPath.PlaceholderText = $"Full path of the program on {remote.ServerName}";
            if (!remote.ProgramSettings)
            {
                _programMode.IsEnabled = false;
                _programMode.Description = $"{remote.ServerName} doesn't let this app set programs it runs.";
            }

            if (!remote.CompletionActions) _openFile.IsEnabled = _systemAction.IsEnabled = false;
        }

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
        else if (_programMode.SelectedIndex == 2 && _remote is not null)
            error = _remote.ProgramSettings ? RemoteDataRules.ServerProgramPathProblem(options.ProgramPath) : null;
        else if (_programMode.SelectedIndex == 2 && !IsAbsoluteExecutable(options.ProgramPath))
            error = "Choose an absolute path to an existing .exe file.";
        // Off for a server that doesn't allow it, so whatever it has is sent back unchanged.
        else if (options.SystemAction != "none" && !_systemActionsEnabled && _remote is not { CompletionActions: false })
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
        var enabled = _programMode.SelectedIndex == 2 && _remote?.ProgramSettings != false;
        _programPath.IsEnabled = enabled;
        _browse.IsEnabled = enabled;
    }

    private async Task BrowseAsync()
    {
        _browse.IsEnabled = false;
        try
        {
            if (await Pickers.PickFilesAsync(WindowNative.GetWindowHandle(_owner), [".exe"], multiple: false, PickerLocationId.ComputerFolder) is not [var file]) return;
            _programPath.Text = file;
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

    /// <summary>This PC's Host only: a program on this PC, so it is looked for here.</summary>
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
