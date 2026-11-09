using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// U-WL-9: Repair from the status bar, for a browser registration the Host reports as broken or missing. It runs the
/// <c>register-native-host.ps1</c> that Setup installed, for this install's <c>app</c> folder and without <c>-Force</c>,
/// so a registration that belongs to another program is reported, never replaced.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan RepairPatience = TimeSpan.FromSeconds(60);
    private bool _repairing;

    private async void RepairBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_repairing) return;
        const string title = "Repair browser integration";
        var plan = ShellWords.RepairPlan(AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA"),
            Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTANCE"),
            File.Exists);
        if (plan.Refusal is { } refusal)
        {
            await ShowMessageAsync(title, refusal);
            return;
        }

        var ask = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = new TextBlock
            {
                Text = $"The background Host says: {_status?.BrowserIntegration}.\n\nRepair registers Inbound with Chrome, Edge and Firefox again, so the extension can reach it. A registration that belongs to another program isn't replaced.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Repair",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await DialogGuard.ShowAsync(ask) != ContentDialogResult.Primary) return;

        _repairing = true;
        RepairBrowserButton.IsEnabled = false;
        try
        {
            var problem = await RunRepairAsync(plan.Script!, plan.AppDirectory!);
            await RefreshAsync();
            if (problem is null) ShowPassingStatus("Browser integration repaired.");
            else await ShowMessageAsync(title, $"The registration couldn't be repaired. {problem}");
        }
        finally
        {
            _repairing = false;
            RepairBrowserButton.IsEnabled = true;
        }
    }

    /// <returns>Null when the script finished well, or what went wrong.</returns>
    private static async Task<string?> RunRepairAsync(string script, string appDirectory)
    {
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-AppDir", appDirectory })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return "Windows PowerShell didn't start.";
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var patience = new CancellationTokenSource(RepairPatience);
            try
            {
                await process.WaitForExitAsync(patience.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return "It took too long and was stopped.";
            }

            await Task.WhenAll(output, error);
            if (process.ExitCode == 0) return null;
            var reason = (await error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(reason) ? $"The script ended with code {process.ExitCode}." : reason;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
