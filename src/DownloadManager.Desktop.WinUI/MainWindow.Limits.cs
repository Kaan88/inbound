using System.Globalization;
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R15, UI.R16: the toolbar's limit menus. Each holds common values and saves the setting at once; the Host
/// applies it live (the speed cap to running downloads, connections to new and unstarted ones).
/// </summary>
public sealed partial class MainWindow
{
    private const long MegabytePerSecond = 1024 * 1024;
    private static readonly int[] DownloadChoices = [1, 2, 3, 4, 5, 6, 8, 10, 12, 16];
    private static readonly int[] ConnectionChoices = [1, 2, 4, 6, 8, 12, 16, 24, 32];
    private static readonly int[] SpeedChoices = [1, 2, 5, 10, 20, 30, 50, 70, 100, 200, 500];
    private bool _savingLimit;

    private void InitializeLimitMenus()
    {
        foreach (var value in DownloadChoices)
            DownloadsLimitMenu.Items.Add(LimitItem($"{value} at a time", "Downloads", value,
                settings => settings.MaxConcurrentDownloads = value));
        foreach (var value in ConnectionChoices)
            ConnectionsLimitMenu.Items.Add(LimitItem(value == 1 ? "1 connection" : $"{value} connections", "Connections", value,
                settings => settings.DefaultConnections = value));

        SpeedLimitMenu.Items.Add(LimitItem("Unlimited", "Speed", 0L, settings => settings.GlobalSpeedLimitBytesPerSecond = null));
        foreach (var value in SpeedChoices)
            SpeedLimitMenu.Items.Add(LimitItem($"{value} MB/s", "Speed", value * MegabytePerSecond,
                settings => settings.GlobalSpeedLimitBytesPerSecond = value * MegabytePerSecond));
        SpeedLimitMenu.Items.Add(new MenuFlyoutSeparator());
        var custom = new MenuFlyoutItem { Text = "Other…" };
        custom.Click += async (_, _) => await AskCustomSpeedAsync();
        SpeedLimitMenu.Items.Add(custom);
        UpdateLimitButtons();
    }

    private RadioMenuFlyoutItem LimitItem(string text, string group, long value, Action<SettingsDto> apply)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "Limit" + group, Tag = value };
        item.Click += async (_, _) => await SaveLimitAsync(apply);
        return item;
    }

    /// <summary>Shows the stored values on the buttons and ticks the matching menu items.</summary>
    private void UpdateLimitButtons()
    {
        if (_settings is not { } settings) return;
        var speed = settings.GlobalSpeedLimitBytesPerSecond is > 0 ? settings.GlobalSpeedLimitBytesPerSecond.Value : 0;
        DownloadsLimitText.Text = settings.MaxConcurrentDownloads == 1 ? "1 download" : $"{settings.MaxConcurrentDownloads} downloads";
        ConnectionsLimitText.Text = settings.DefaultConnections == 1 ? "1 connection" : $"{settings.DefaultConnections} connections";
        SpeedLimitText.Text = speed == 0 ? "No speed limit" : SpeedText(speed);
        Tick(DownloadsLimitMenu, settings.MaxConcurrentDownloads);
        Tick(ConnectionsLimitMenu, settings.DefaultConnections);
        Tick(SpeedLimitMenu, speed);
    }

    private static void Tick(MenuFlyout menu, long value)
    {
        foreach (var item in menu.Items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked = item.Tag is long tag && tag == value;
    }

    internal static string SpeedText(long bytesPerSecond)
    {
        var megabytes = bytesPerSecond / (double)MegabytePerSecond;
        return megabytes >= 1
            ? $"{megabytes.ToString("0.##", CultureInfo.CurrentCulture)} MB/s"
            : $"{(bytesPerSecond / 1024d).ToString("0.#", CultureInfo.CurrentCulture)} KB/s";
    }

    private async Task AskCustomSpeedAsync()
    {
        var current = _settings?.GlobalSpeedLimitBytesPerSecond is > 0 ? _settings.GlobalSpeedLimitBytesPerSecond.Value / (double)MegabytePerSecond : double.NaN;
        var box = new NumberBox
        {
            Header = "Total speed for all downloads (MB/s)",
            Minimum = 0.1,
            Maximum = 100_000,
            SmallChange = 1,
            LargeChange = 10,
            Value = current,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 0, IsDecimalPointAlwaysDisplayed = false },
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Speed limit",
            Content = box,
            PrimaryButtonText = "Set",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = RootGrid.ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || double.IsNaN(box.Value) || box.Value <= 0)
        {
            UpdateLimitButtons();
            return;
        }
        var bytes = (long)Math.Round(box.Value * MegabytePerSecond);
        await SaveLimitAsync(settings => settings.GlobalSpeedLimitBytesPerSecond = bytes);
    }

    private async Task SaveLimitAsync(Action<SettingsDto> apply)
    {
        if (_savingLimit) return;
        _savingLimit = true;
        try
        {
            // From the Host's latest settings, so nothing changed elsewhere since the last snapshot is undone.
            var latest = await _connection.RefreshAsync();
            var updated = CloneSettings(latest.Settings);
            apply(updated);
            var reply = await _connection.UpdateSettingsAsync(updated);
            var result = reply.GetPayload<SettingsResult>();
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? "The limit was not saved.");
            _settings = result.Settings;
        }
        catch (Exception ex)
        {
            ShowLimitError(ex.Message);
        }
        finally
        {
            _savingLimit = false;
            UpdateLimitButtons();
        }
    }

    /// <summary>
    /// UI.B3: the Host doesn't send settings changes to the window, so a default folder changed from the CLI or
    /// by an import stayed old in Add URL and Add Links. They read the Host's current settings when they open.
    /// </summary>
    private async Task RefreshSettingsAsync()
    {
        try
        {
            var latest = await _connection.RefreshAsync();
            if (latest.Settings is not null)
            {
                _settings = latest.Settings;
                UpdateLimitButtons();
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // Not connected just now: the copy from the last snapshot is the best there is.
        }
    }

    private void ShowLimitError(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "The limit was not changed",
            Content = message,
            CloseButtonText = "OK",
            RequestedTheme = RootGrid.ActualTheme,
        };
        _ = dialog.ShowAsync();
    }
}
