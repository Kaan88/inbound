using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.AvaloniaUi;

public sealed partial class TorBoxPanel : UserControl
{
    private const string Provider = "torbox";
    private const int PageSize = 50;
    private RemoteClient? _client;
    private CancellationToken _lifetime;
    private bool _busy;
    private bool _hasKey;
    private bool _hasMore;
    private int _offset;
    private RemoteItemDto? _item;
    private string Kind => (Kinds.SelectedItem as TabStripItem)?.Tag as string ?? "torrents";

    public TorBoxPanel()
    {
        InitializeComponent();
        UpdateActions();
    }

    internal async Task OpenAsync(RemoteClient client, CancellationToken lifetime)
    {
        if (_busy) return;
        _client = client;
        _lifetime = lifetime;
        await RunAsync(async token =>
        {
            await ReadAccountAsync(false, token);
            if (_hasKey) await LoadPageAsync(token);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _client is null || _lifetime.IsCancellationRequested) return;
        _busy = true;
        Notice.Text = "Working…";
        UpdateActions();
        try { await action(_lifetime); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { Notice.Text = "Could not complete the TorBox request."; }
        finally { _busy = false; UpdateActions(); }
    }

    private void UpdateActions()
    {
        var ready = !_busy && _client is not null;
        Kinds.IsEnabled = Catalog.IsEnabled = ready && _hasKey;
        AccountEditor.IsEnabled = ready;
        PreviousPage.IsEnabled = ready && _hasKey && _offset > 0;
        NextPage.IsEnabled = ready && _hasKey && _hasMore;
        DownloadAll.IsEnabled = ready && _item is not null;
        DownloadSelected.IsEnabled = DownloadAll.IsEnabled &&
            Files.Items.OfType<CheckBox>().Any(file => file.IsEnabled && file.IsChecked == true);
    }

    private void ApplyAccount(RemoteAccountResult result)
    {
        var account = result.Accounts.FirstOrDefault(value => value.Provider == Provider);
        _hasKey = account is { HasKey: true, KeyUnreadable: false };
        AccountStatus.Text = account?.KeyUnreadable == true ? "Saved key is unreadable"
            : _hasKey ? account?.Plan is { Length: > 0 } plan ? $"Key saved · {plan}" : "Key saved"
            : "No key saved";
        Notice.Text = result.Error ?? (_hasKey ? string.Empty : "Save a TorBox API key under Account.");
        AccountEditor.IsVisible = !_hasKey;
        if (!_hasKey)
        {
            Catalog.Items.Clear();
            ClearItem();
        }
    }

    private async Task ReadAccountAsync(bool fresh, CancellationToken token) =>
        ApplyAccount(await _client!.GetAccountsAsync(Provider, refresh: fresh, cancellationToken: token));

    private async Task LoadPageAsync(CancellationToken token, bool fresh = false)
    {
        var page = await _client!.GetItemsAsync(new RemoteItemRequest
        {
            Provider = Provider, Kind = Kind, Offset = _offset, Limit = PageSize, Fresh = fresh
        }, token);
        Catalog.Items.Clear();
        ClearItem();
        _hasMore = page.Ok && page.HasMore;
        foreach (var item in page.Items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,100,95"), Margin = new Thickness(4, 0) };
            var name = new TextBlock { Text = item.Name ?? item.ItemId, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            var status = new TextBlock { Text = item.Stage ?? item.RawState ?? "—" };
            var size = new TextBlock { Text = item.Size > 0 ? UiFormat.Bytes(item.Size) : "—" };
            Grid.SetColumn(status, 1);
            Grid.SetColumn(size, 2);
            row.Children.Add(name);
            row.Children.Add(status);
            row.Children.Add(size);
            Catalog.Items.Add(new ListBoxItem { Content = row, Tag = item });
        }
        Notice.Text = page.Error ?? (page.Items.Count == 0 ? "No items in this catalog." : $"Page {_offset / PageSize + 1}");
    }

    private void ClearItem()
    {
        _item = null;
        ItemTitle.Text = "Select an item";
        Files.Items.Clear();
    }

    private async void Item_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_busy || Catalog.SelectedItem is not ListBoxItem { Tag: RemoteItemDto item }) return;
        ClearItem();
        await RunAsync(async token =>
        {
            var result = await _client!.GetItemsAsync(new RemoteItemRequest
            {
                Provider = Provider, Kind = item.Kind, ItemId = item.ItemId
            }, token);
            _item = result.Ok ? result.Items.FirstOrDefault() : null;
            if (_item is null)
            {
                Notice.Text = result.Error ?? "This item is unavailable.";
                return;
            }
            ItemTitle.Text = _item.Name ?? _item.ItemId;
            foreach (var file in _item.Files ?? [])
            {
                var box = new CheckBox
                {
                    Content = file.RelativePath ?? file.Note ?? "Unavailable file",
                    Tag = file.FileId,
                    IsEnabled = file.Selectable && !file.Infected
                };
                box.IsCheckedChanged += (_, _) => UpdateActions();
                Files.Items.Add(box);
            }
            Notice.Text = Files.Items.Count == 0 ? "TorBox has not listed any files yet." : string.Empty;
        });
    }

    private void Account_Click(object? sender, RoutedEventArgs e) => AccountEditor.IsVisible = !AccountEditor.IsVisible;
    private async void Refresh_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(async token => { await ReadAccountAsync(true, token); if (_hasKey) await LoadPageAsync(token, true); });
    private async void SaveKey_Click(object? sender, RoutedEventArgs e)
    {
        if (ApiKey.Text?.Trim() is not { Length: > 0 } key) return;
        ApiKey.Clear();
        await RunAsync(async token =>
        {
            ApplyAccount(await _client!.SetKeyAsync(key, Provider, token));
            if (_hasKey) await LoadPageAsync(token);
        });
    }

    private async void RemoveKey_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner ||
            await MessageDialog.ConfirmAsync(owner, "Remove TorBox key?",
                "The Host will forget the saved TorBox API key.", "Remove key", null) is null) return;
        await RunAsync(async token => ApplyAccount(await _client!.ClearKeyAsync(Provider, token)));
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage?.CanPickFolder != true) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Download destination" });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) Destination.Text = path;
    }

    private async void Kind_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_hasKey || _busy) return;
        _offset = 0;
        await RunAsync(token => LoadPageAsync(token));
    }
    private async void Previous_Click(object? sender, RoutedEventArgs e)
    {
        _offset = Math.Max(0, _offset - PageSize);
        await RunAsync(token => LoadPageAsync(token));
    }
    private async void Next_Click(object? sender, RoutedEventArgs e)
    {
        _offset += PageSize;
        await RunAsync(token => LoadPageAsync(token));
    }

    private async void DownloadSelected_Click(object? sender, RoutedEventArgs e) => await AddAsync(false);
    private async void DownloadAll_Click(object? sender, RoutedEventArgs e) => await AddAsync(true);

    private Task AddAsync(bool all)
    {
        if (_item is not { } item) return Task.CompletedTask;
        var destination = Destination.Text?.Trim();
        if (!string.IsNullOrEmpty(destination) && !Path.IsPathFullyQualified(destination))
        {
            Notice.Text = "Enter an absolute destination path or leave it blank.";
            return Task.CompletedTask;
        }
        var ids = Files.Items.OfType<CheckBox>().Where(file => file.IsEnabled && file.IsChecked == true)
            .Select(file => file.Tag as string).OfType<string>().ToList();
        if (!all && ids.Count == 0) return Task.CompletedTask;
        return RunAsync(async token =>
        {
            var result = await _client!.AddAsync(new RemoteAddRequestDto
            {
                Provider = Provider, ItemKind = item.Kind, ItemId = item.ItemId,
                Destination = string.IsNullOrEmpty(destination) ? null : destination,
                Selection = all ? "all" : "chosen", FileIds = all ? null : ids
            }, token);
            Notice.Text = result.Error ?? result.Note ?? (result.Ok ? "Download requested." : "TorBox did not accept the request.");
        });
    }
}
