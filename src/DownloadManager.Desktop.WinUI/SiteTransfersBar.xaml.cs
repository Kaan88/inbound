using System.Collections.ObjectModel;
using System.ComponentModel;
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One batch in the Transfers bar. Public because the row's bindings read it by reflection; updated in place,
/// so a row keeps its focus while its figures change each second.</summary>
public sealed class SiteTransferRow : INotifyPropertyChanged
{
    internal SiteTransferRow(SiteTransferDto transfer) => Update(transfer);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; private set; } = "";
    public string Glyph { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Route { get; private set; } = "";
    public string StateText { get; private set; } = "";
    public double Percent { get; private set; }
    public Visibility ProgressVisibility { get; private set; }
    public Visibility PauseVisibility { get; private set; }
    public Visibility ResumeVisibility { get; private set; }
    public Visibility RetryVisibility { get; private set; }
    public Visibility CancelVisibility { get; private set; }
    public Visibility ClearVisibility { get; private set; }
    public string AccessibleName => $"{Title}. {StateText}";

    internal void Update(SiteTransferDto t)
    {
        Id = t.Id;
        // Segoe MDL2 download and upload arrows.
        Glyph = SiteTransferRules.IsDownload(t) ? "" : "";
        Title = SiteTransferRules.Title(t);
        Route = SiteTransferRules.Route(t);
        StateText = SiteTransferRules.StateText(t);
        Percent = t.Bytes > 0 ? Math.Clamp(100.0 * t.BytesDone / t.Bytes, 0, 100)
            : t.Files > 0 ? 100.0 * (t.FilesDone + t.FilesSkipped) / t.Files : 0;
        ProgressVisibility = t.State is "running" or "paused" ? Visibility.Visible : Visibility.Collapsed;
        PauseVisibility = Show(SiteTransferRules.CanPause(t));
        ResumeVisibility = Show(SiteTransferRules.CanResume(t));
        RetryVisibility = Show(SiteTransferRules.CanRetry(t));
        CancelVisibility = Show(SiteTransferRules.CanCancel(t));
        ClearVisibility = Show(SiteTransferRules.CanClear(t));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// SITES.4: the Transfers bar (remote-sites.md, "The agreed design in short"). It shows the batches the Host pushes and
/// asks the window to act on them; it never talks to the Host itself.
/// </summary>
public sealed partial class SiteTransfersBar : UserControl
{
    private readonly ObservableCollection<SiteTransferRow> _rows = [];
    private List<SiteTransferDto> _transfers = [];
    private string _tab = SiteTransferRules.ActiveTab;
    private bool _open;

    public SiteTransfersBar()
    {
        InitializeComponent();
        RowsList.ItemsSource = _rows;
        Show(_transfers);
    }

    /// <summary>Pause, resume, cancel, retry or clear (a message type) on a batch id; the answer says whether it worked.</summary>
    internal Func<string, string, Task<SiteTransferResultDto>>? Act { get; set; }

    /// <summary>One batch with its files.</summary>
    internal Func<string, Task<SiteTransferResultDto>>? Details { get; set; }

    internal bool IsOpen => _open;

    /// <summary>Opens or closes the list above the bar.</summary>
    internal void SetOpen(bool open)
    {
        _open = open;
        BodyPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        // Up when closed (it opens upward), down when open.
        Chevron.Glyph = open ? "" : "";
        AutomationProperties.SetName(ToggleButton, open ? "Hide transfers" : "Show transfers");
    }

    /// <summary>Opens on the tab that has the newest batch worth looking at: failures first, then what runs.</summary>
    internal void OpenAtBest()
    {
        var tab = _transfers.Any(t => t.State == "failed") ? SiteTransferRules.FailedTab : SiteTransferRules.ActiveTab;
        SelectTab(tab);
        SetOpen(true);
    }

    /// <summary>Every batch the Host has now.</summary>
    internal void Show(IReadOnlyList<SiteTransferDto> transfers)
    {
        _transfers = [.. transfers];
        SummaryText.Text = SiteTransferRules.Summary(_transfers) is { } summary ? "· " + summary["Site transfers: ".Length..] : "";
        ActiveTabButton.Content = SiteTransferRules.TabLabel(_transfers, SiteTransferRules.ActiveTab);
        FailedTabButton.Content = SiteTransferRules.TabLabel(_transfers, SiteTransferRules.FailedTab);
        DoneTabButton.Content = SiteTransferRules.TabLabel(_transfers, SiteTransferRules.DoneTab);
        ClearFinishedButton.IsEnabled = _transfers.Any(SiteTransferRules.CanClear);
        var retryAll = _tab == SiteTransferRules.FailedTab && SiteTransferRules.RetryAll(_transfers).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (RetryAllButton.Visibility != retryAll) RetryAllButton.Visibility = retryAll;

        var wanted = SiteTransferRules.InTab(_transfers, _tab);
        // Rows are kept and updated in place; only those that left or came are removed or added.
        for (var index = _rows.Count - 1; index >= 0; index--)
            if (wanted.All(t => t.Id != _rows[index].Id)) _rows.RemoveAt(index);
        for (var index = 0; index < wanted.Count; index++)
        {
            var transfer = wanted[index];
            var at = IndexOf(transfer.Id);
            if (at < 0) _rows.Insert(index, new SiteTransferRow(transfer));
            else
            {
                _rows[at].Update(transfer);
                if (at != index) _rows.Move(at, index);
            }
        }

        EmptyText.Text = _rows.Count > 0 ? "" : _tab switch
        {
            SiteTransferRules.FailedTab => "No failed transfers.",
            SiteTransferRules.DoneTab => "No finished transfers.",
            _ => "Nothing is moving. Select files on a site and choose Download, or choose Upload.",
        };
    }

    private int IndexOf(string id)
    {
        for (var index = 0; index < _rows.Count; index++) if (_rows[index].Id == id) return index;
        return -1;
    }

    private void SelectTab(string tab)
    {
        _tab = tab;
        ActiveTabButton.IsChecked = tab == SiteTransferRules.ActiveTab;
        FailedTabButton.IsChecked = tab == SiteTransferRules.FailedTab;
        DoneTabButton.IsChecked = tab == SiteTransferRules.DoneTab;
        _rows.Clear();
        Show(_transfers);
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) => SetOpen(!_open);

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tab }) SelectTab(tab);
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => _ = ActAsync(sender, SitesMessageTypes.TransferPause);
    private void ResumeButton_Click(object sender, RoutedEventArgs e) => _ = ActAsync(sender, SitesMessageTypes.TransferResume);
    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = ActAsync(sender, SitesMessageTypes.TransferRetry);
    private void CancelButton_Click(object sender, RoutedEventArgs e) => _ = ActAsync(sender, SitesMessageTypes.TransferCancel);
    private void ClearButton_Click(object sender, RoutedEventArgs e) => _ = ActAsync(sender, SitesMessageTypes.TransferClear);

    private void ClearFinishedButton_Click(object sender, RoutedEventArgs e) => _ = RunAsync(ClearFinishedButton, SitesMessageTypes.TransferClear, "");

    private async void RetryAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Act is not { } act) return;
        ErrorText.Text = "";
        RetryAllButton.IsEnabled = false;
        try
        {
            var problems = new List<string>();
            foreach (var id in SiteTransferRules.RetryAll(_transfers))
            {
                var reply = await act(SitesMessageTypes.TransferRetry, id);
                if (!reply.Ok && reply.Message is { Length: > 0 } message && !problems.Contains(message)) problems.Add(message);
            }

            if (problems.Count > 0) ErrorText.Text = string.Join(" ", problems);
        }
        finally
        {
            RetryAllButton.IsEnabled = true;
        }
    }

    private Task ActAsync(object sender, string type) =>
        sender is Button { Tag: string id } button ? RunAsync(button, type, id) : Task.CompletedTask;

    private async Task RunAsync(Control button, string type, string id)
    {
        if (Act is not { } act) return;
        ErrorText.Text = "";
        button.IsEnabled = false;
        try
        {
            var reply = await act(type, id);
            if (!reply.Ok) ErrorText.Text = reply.Message ?? "That didn't work.";
        }
        finally
        {
            // Clear finished stays off when nothing is left to clear; Show may have run meanwhile.
            button.IsEnabled = button != ClearFinishedButton || _transfers.Any(SiteTransferRules.CanClear);
        }
    }

    private async void FilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || Details is not { } details || XamlRoot is null) return;
        var reply = await details(id);
        if (!reply.Ok || reply.Items is not { } items)
        {
            ErrorText.Text = reply.Message ?? "The files of that transfer could not be read.";
            return;
        }

        var list = new StackPanel { Spacing = 6 };
        foreach (var item in items.OrderBy(i => i.State != "failed").ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
        {
            var line = new StackPanel { Spacing = 1 };
            var name = item.Target is { } target && target != item.Path ? $"{item.Path} → {target}" : item.Path;
            line.Children.Add(new TextBlock { Text = name, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var state = SiteTransferRules.ItemText(item);
            line.Children.Add(new TextBlock { Text = state, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            list.Children.Add(line);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = reply.Transfer is { } transfer ? SiteTransferRules.Title(transfer) : "Files",
            Content = new ScrollViewer { Content = list, MaxHeight = 420 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        await DialogGuard.ShowAsync(dialog);
    }
}
