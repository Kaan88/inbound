using System.Diagnostics;
using System.Globalization;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// One progress window for a whole package: overall progress plus a row per file. The Host opens
/// it instead of one window per member; double-clicking a file opens that file's own window.
/// Closing it never stops the Host or the downloads.
/// </summary>
internal sealed class PackageWindow : Window
{
    private readonly Guid _packageId;
    private readonly HostConnection _connection = new();
    private readonly Dictionary<Guid, JobDto> _jobs = [];
    private readonly Dictionary<Guid, Row> _rows = [];
    private List<Guid> _members = [];
    private bool _closed;

    private readonly Grid _root = new() { Padding = new Thickness(18), RowSpacing = 10 };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _summary = new() { Opacity = 0.8 };
    private readonly ProgressBar _overall = new() { Minimum = 0, Maximum = 100, Height = 6 };
    private readonly TextBlock _detail = new() { Opacity = 0.8 };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Button _pause = new() { Content = "Pause all", MinWidth = 100 };
    private readonly Button _resume = new() { Content = "Resume all", MinWidth = 100 };
    private readonly Button _openFolder = new() { Content = "Open folder", MinWidth = 100, Visibility = Visibility.Collapsed };
    private readonly Button _close = new() { Content = "Hide", MinWidth = 92 };

    private sealed record Row(Grid Grid, TextBlock Name, TextBlock Status, ProgressBar Bar, TextBlock Size);

    public PackageWindow(Guid packageId)
    {
        _packageId = packageId;
        var theme = UiStateStore.Load().DarkMode;
        _root.RequestedTheme = theme is { } dark ? (dark ? ElementTheme.Dark : ElementTheme.Light) : ElementTheme.Default;
        Title = "Package status — Inbound Download Manager";
        WindowLayout.ResizeForDpi(this, 720, 520);
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _root.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(_title);
        header.Children.Add(_summary);
        _root.Children.Add(header);
        Add(_overall, 1);
        Add(_detail, 2);
        var listFrame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4),
            Child = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Add(listFrame, 3);
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_notice);
        var column = 1;
        foreach (var button in new[] { _openFolder, _pause, _resume, _close })
        {
            Grid.SetColumn(button, column++);
            footer.Children.Add(button);
        }
        Add(footer, 4);

        void ApplyColors()
        {
            _root.Background = InboundTheme.Brush("InboundSurfaceBrush", _root);
            listFrame.BorderBrush = InboundTheme.Brush("InboundDividerBrush", _root);
        }
        _root.Loaded += (_, _) => ApplyColors();
        _root.ActualThemeChanged += (_, _) => ApplyColors();

        _pause.Click += async (_, _) => await ForEachMemberAsync(job => job.CanPause, id => _connection.PauseAsync(id));
        _resume.Click += async (_, _) => await ForEachMemberAsync(job => job.CanResume, id => _connection.ResumeAsync(id));
        _openFolder.Click += (_, _) => OpenFolder();
        _close.Click += (_, _) => Close();

        _connection.HostExiting += () => DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
        _connection.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _jobs.Clear();
            foreach (var job in snapshot.Jobs) _jobs[job.Id] = job;
            SetMembers(snapshot.Settings.DownloadGroups);
        });
        _connection.GroupsChanged += groups => DispatcherQueue.TryEnqueue(() => { if (!_closed) SetMembers(groups); });
        _connection.JobChanged += (job, type) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (type == "removed") _jobs.Remove(job.Id);
            else _jobs[job.Id] = job;
            if (_members.Contains(job.Id)) Render();
        });
        _connection.ConnectionChanged += (connected, error) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _notice.Text = connected ? string.Empty : $"Host disconnected. Reconnecting… {error}";
        });
        Closed += async (_, _) =>
        {
            _closed = true;
            await _connection.DisposeAsync();
        };
        _root.KeyboardAccelerators.Add(Escape());
        Content = _root;
        WindowLayout.ApplyChrome(this, _root);
        Render();
        _connection.Start();
    }

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void Add(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        _root.Children.Add(element);
    }

    private KeyboardAccelerator Escape()
    {
        var accelerator = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        accelerator.Invoked += (_, args) => { args.Handled = true; Close(); };
        return accelerator;
    }

    private void SetMembers(List<DownloadGroupDto> groups)
    {
        var group = groups.FirstOrDefault(candidate => candidate.Id == _packageId);
        if (group is null)
        {
            _members = [];
            _title.Text = "Package removed";
            _notice.Text = "This package no longer exists. Its files are listed individually in the main window.";
        }
        else
        {
            _title.Text = group.Name;
            _members = [.. group.JobIds];
        }
        Render();
    }

    private void Render()
    {
        var members = _members.Where(_jobs.ContainsKey).Select(id => _jobs[id])
            .OrderBy(job => job.FileName, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var stale in _rows.Keys.Except(members.Select(job => job.Id)).ToList())
        {
            _list.Children.Remove(_rows[stale].Grid);
            _rows.Remove(stale);
        }
        for (var index = 0; index < members.Count; index++)
        {
            var job = members[index];
            if (!_rows.TryGetValue(job.Id, out var row)) _rows[job.Id] = row = CreateRow(job.Id);
            var at = _list.Children.IndexOf(row.Grid);
            if (at != index)
            {
                if (at >= 0) _list.Children.RemoveAt(at);
                _list.Children.Insert(Math.Min(index, _list.Children.Count), row.Grid);
            }
            row.Name.Text = job.FileName;
            row.Status.Text = StateText(job);
            row.Bar.IsIndeterminate = job.TotalBytes is not > 0 && job.State is "connecting" or "downloading";
            row.Bar.Value = job.State == "completed" ? 100 : Percent(job.ReceivedBytes, job.TotalBytes);
            row.Size.Text = job.TotalBytes is > 0 ? $"{Bytes(job.ReceivedBytes)} / {Bytes(job.TotalBytes.Value)}" : Bytes(job.ReceivedBytes);
        }

        var total = members.Sum(job => job.TotalBytes ?? 0);
        var received = members.Sum(job => job.State == "completed" && job.TotalBytes is > 0 ? job.TotalBytes.Value : job.ReceivedBytes);
        var completed = members.Count(job => job.State == "completed");
        var speed = members.Sum(job => job.State is "downloading" ? job.BytesPerSecond ?? 0 : 0);
        var allKnown = members.Count > 0 && members.All(job => job.TotalBytes is > 0);
        _summary.Text = members.Count == 0 ? string.Empty : $"{completed} of {members.Count} files complete";
        _overall.Value = members.Count > 0 && completed == members.Count ? 100 : allKnown ? Percent(received, total) : 0;
        _overall.IsIndeterminate = !allKnown && completed < members.Count && speed > 0;
        var remaining = allKnown && speed > 0 ? TimeSpan.FromSeconds((total - received) / speed) : (TimeSpan?)null;
        _detail.Text = members.Count == 0 ? string.Empty : string.Join("   ·   ", new[]
        {
            allKnown ? $"{Bytes(received)} of {Bytes(total)}" : Bytes(received),
            speed > 0 ? $"{Bytes((long)speed)}/s" : null,
            remaining is { } left ? $"{FormatDuration(left)} left" : null,
        }.Where(part => part is not null));

        var allDone = members.Count > 0 && completed == members.Count;
        _openFolder.Visibility = allDone ? Visibility.Visible : Visibility.Collapsed;
        _pause.IsEnabled = members.Any(job => job.CanPause);
        _resume.IsEnabled = members.Any(job => job.CanResume);
        _close.Content = allDone ? "Close" : "Hide";
        if (allDone) Title = "Package complete — Inbound Download Manager";
    }

    private Row CreateRow(Guid jobId)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { Opacity = 0.8 };
        var size = new TextBlock { Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Right };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(status, 1);
        Grid.SetColumn(size, 2);
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 3);
        grid.Children.Add(name);
        grid.Children.Add(status);
        grid.Children.Add(size);
        grid.Children.Add(bar);
        ToolTipService.SetToolTip(grid, "Double-click for this file's progress details");
        grid.DoubleTapped += (_, _) => OpenFileWindow(jobId);
        return new Row(grid, name, status, bar, size);
    }

    private async Task ForEachMemberAsync(Func<JobDto, bool> eligible, Func<Guid, Task<IpcMessage>> action)
    {
        var targets = _members.Where(id => _jobs.TryGetValue(id, out var job) && eligible(job)).ToList();
        var failed = 0;
        foreach (var id in targets)
        {
            try
            {
                var reply = await action(id);
                if (reply.Type == "error") failed++;
            }
            catch (Exception) { failed++; }
        }
        _notice.Text = failed == 0 ? string.Empty : $"{failed} file(s) could not be changed.";
    }

    private void OpenFileWindow(Guid jobId)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--job {jobId:D} --manual") { UseShellExecute = false });
        }
        catch (Exception ex) { _notice.Text = $"Could not open progress details: {ex.Message}"; }
    }

    private void OpenFolder()
    {
        var folder = _members.Select(id => _jobs.TryGetValue(id, out var job) ? Path.GetDirectoryName(job.FinalFilePath) : null)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
        if (folder is null) { _notice.Text = "The download folder was not found."; return; }
        try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
        catch (Exception ex) { _notice.Text = $"Could not open the folder: {ex.Message}"; }
    }

    private static string StateText(JobDto job) => job.State switch
    {
        "downloading" => job.TotalBytes is > 0 ? $"{Percent(job.ReceivedBytes, job.TotalBytes):0}%" : "Downloading",
        "connecting" => "Connecting",
        "queued" => "Queued",
        "paused" => "Paused",
        "completed" => "Complete",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        "retryWaiting" or "retrying" => "Retrying",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(job.State),
    };

    private static double Percent(long received, long? total)
        => total is > 0 ? Math.Clamp(received * 100.0 / total.Value, 0, 100) : 0;

    private static string Bytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):N2} GB" : bytes >= 1024L * 1024
        ? $"{bytes / (1024.0 * 1024):N1} MB" : bytes >= 1024 ? $"{bytes / 1024.0:N0} KB" : $"{bytes:N0} B";

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
        return duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s" : $"{duration.Seconds}s";
    }
}
