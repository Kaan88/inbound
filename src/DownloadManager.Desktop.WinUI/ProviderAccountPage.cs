using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// One provider's Options page, built from what <c>remote.account.get</c> says. The key is write-only:
/// it stays in the password box until it is sent and is then gone from this window, and the box has no
/// reveal button. Saving, validating and clearing happen straight away, apart from OK and Cancel.
/// </summary>
internal sealed class ProviderAccountPage
{
    private readonly RemoteClient _client;
    private readonly string _provider;
    private readonly string _name;
    private readonly CancellationTokenSource _closing = new();
    private readonly PasswordBox _keyBox;
    private readonly Button _save = new() { Content = "Save key", MinWidth = 92 };
    private readonly Button _validate = new() { Content = "Validate", MinWidth = 92 };
    private readonly Button _clear = new() { Content = "Clear key…", MinWidth = 92 };
    private readonly StatusLine _keyStatus;
    private readonly StatusLine _details;
    private readonly StatusLine _result;
    private ProviderAccountView _view;
    private bool _busy;

    /// <param name="serverName">The server whose account this is, while the window shows one (U-WR-6); null for this PC.</param>
    /// <param name="hostKind">That server's kind (<see cref="HostKinds"/>), which says how it keeps the key.</param>
    public ProviderAccountPage(RemoteClient client, RemoteAccountDto account, string? serverName = null, string? hostKind = null)
    {
        _client = client;
        _provider = account.Provider;
        _name = string.IsNullOrWhiteSpace(account.ProviderName) ? account.Provider : account.ProviderName;
        _view = RemoteText.Account(account, DateTimeOffset.Now);
        Title = _view.PageTitle;

        _keyStatus = new StatusLine($"{_name} API key status");
        _details = new StatusLine($"{_name} account details");
        _result = new StatusLine($"{_name} account result");

        _keyBox = new PasswordBox { Header = _view.KeyBoxHeader, PasswordRevealMode = PasswordRevealMode.Hidden };
        AutomationProperties.SetName(_keyBox, $"{_name} API key");
        _keyBox.PasswordChanged += (_, _) =>
        {
            UpdateButtons();
            if (_keyBox.Password.Length == 0) KeyBoxEmptied?.Invoke();
        };
        _keyBox.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter && _save.IsEnabled)
            {
                args.Handled = true;
                _ = SaveAsync();
            }
        };

        AutomationProperties.SetName(_save, $"Save the {_name} API key");
        AutomationProperties.SetName(_validate, $"Validate the saved key with {_name}");
        AutomationProperties.SetName(_clear, $"Clear the saved {_name} API key");
        _save.Click += (_, _) => _ = SaveAsync();
        _validate.Click += (_, _) => _ = ValidateAsync();
        _clear.Click += (_, _) => _ = ClearAsync();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_save);
        buttons.Children.Add(_validate);
        buttons.Children.Add(_clear);

        var controls = new List<UIElement>();
        if (serverName is not null)
            controls.Add(SettingsWindow.Hint($"{_name} account of {serverName}. What is changed here changes it on {serverName}, not on this PC."));
        controls.AddRange(
        [
            SettingsWindow.Section("API key"),
            _keyStatus.Text,
            _keyBox,
            buttons,
            _result.Text,
            SettingsWindow.Hint(RemoteOptionsText.ProviderKeyNote(serverName, hostKind)),
            SettingsWindow.Section("Account"),
            _details.Text,
        ]);
        Controls = controls.ToArray();

        Render();
        _client.State.AccountChanged += OnAccountChanged;
    }

    /// <summary>The box no longer holds a key that OK would have complained about.</summary>
    public event Action? KeyBoxEmptied;

    public string Title { get; }

    public string ProviderName => _name;

    public UIElement[] Controls { get; }

    /// <summary>Something was typed and not saved. OK shouldn't silently lose it.</summary>
    public bool HasUnsavedKey => !string.IsNullOrEmpty(_keyBox.Password);

    /// <summary>Ends a call still running, by closing its connection, and forgets what was typed.</summary>
    public void Close()
    {
        _client.State.AccountChanged -= OnAccountChanged;
        _closing.Cancel();
        _keyBox.Password = string.Empty;
    }

    private void OnAccountChanged(RemoteAccountDto account)
    {
        if (!string.Equals(account.Provider, _provider, StringComparison.OrdinalIgnoreCase)) return;
        _view = RemoteText.Account(account, DateTimeOffset.Now);
        Render();
    }

    private void Render()
    {
        _keyStatus.Show(_view.KeyStatus, _view.KeyStatusIsProblem);
        _keyBox.Header = _view.KeyBoxHeader;
        _details.Show(_view.AccountDetails.Length > 0 ? _view.AccountDetails : "Nothing to show until a key is saved and validated.");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _keyBox.IsEnabled = !_busy;
        _save.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(_keyBox.Password);
        _validate.IsEnabled = !_busy && _view.CanValidate;
        _clear.IsEnabled = !_busy && _view.CanClear;
    }

    private async Task SaveAsync()
    {
        var key = _keyBox.Password;
        if (string.IsNullOrWhiteSpace(key)) return;

        // Out of the box before anything is sent, whatever the answer turns out to be.
        _keyBox.Password = string.Empty;
        await RunAsync("Saving the key…", async token =>
        {
            var result = await _client.SetKeyAsync(key, _provider, token);
            key = string.Empty;
            return result.Ok
                ? ($"Key saved. Select Validate to check it with {_name}.", false)
                : (Failed(result), true);
        });
    }

    private Task ValidateAsync() => RunAsync($"Asking {_name}…", async token =>
    {
        var result = await _client.ValidateAsync(_provider, token);
        return result.Ok
            ? (RemoteText.Validated(result.Accounts.FirstOrDefault()), false)
            : (Failed(result), true);
    });

    private async Task ClearAsync()
    {
        if (!await ConfirmClearAsync()) return;
        await RunAsync("Clearing the key…", async token =>
        {
            var result = await _client.ClearKeyAsync(_provider, token);
            return result.Ok ? ("Key cleared.", false) : (Failed(result), true);
        });
    }

    private async Task<bool> ConfirmClearAsync()
    {
        _busy = true;
        UpdateButtons();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _clear.XamlRoot,
                RequestedTheme = _clear.ActualTheme,
                Title = $"Clear the {_name} API key?",
                Content = new TextBlock
                {
                    Text = $"This app can't reach your {_name} account until a key is saved again, so work waiting on {_name} stops being checked. Nothing on your {_name} account is deleted.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Clear key",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            // DialogGuard never throws: a dialog it couldn't show comes back as null, which says why here.
            var answer = await DialogGuard.ShowAsync(dialog);
            if (answer is null)
            {
                _result.Show("The key wasn't cleared, because another dialog is open. Close it and try again.", isProblem: true);
                return false;
            }

            return answer == ContentDialogResult.Primary;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private async Task RunAsync(string working, Func<CancellationToken, Task<(string Text, bool IsProblem)>> call)
    {
        _busy = true;
        UpdateButtons();
        _result.Show(working);
        try
        {
            var (text, isProblem) = await call(_closing.Token);
            _result.Show(text, isProblem);
        }
        catch (OperationCanceledException)
        {
            // The window closed, and there is nothing left to show it in.
            return;
        }

        _busy = false;
        UpdateButtons();
    }

    private string Failed(RemoteAccountResult result) => RemoteText.Failure(result.Error, result.FailureKind, _name);
}
