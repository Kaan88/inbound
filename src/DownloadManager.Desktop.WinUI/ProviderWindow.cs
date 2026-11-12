using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R32: My provider in a window of its own, for a second screen. It holds the same <see cref="ProviderPanel"/> the
/// main window shows, in its full layout, and takes up the place the main window's view had. A Download closes it
/// (TB.F13) and the main window shows the notice; Esc and Close close it.
/// </summary>
internal sealed class ProviderWindow : Window
{
    /// <summary>All buttons of the details pane fit beside a usable list at this width.</summary>
    private const int WindowMinimumWidth = 880;

    private const int WindowMinimumHeight = 560;

    public ProviderWindow(RemoteClient client, RemoteAccountDto account, Func<Window, ProviderPanelContext> context)
    {
        Panel = new ProviderPanel(client, account, context(this), inMainWindow: false);
        var root = Panel.Element as Microsoft.UI.Xaml.Controls.Grid ?? throw new InvalidOperationException("The panel's root is a grid.");
        AppTheme.Track(root);
        Title = ServerNames.ForWindow($"My {Panel.ProviderName} — Inbound Download Manager", ShownServer.Target);
        WindowLayout.SetMinimumSize(this, WindowMinimumWidth, WindowMinimumHeight);
        WindowLayout.ResizeForDpi(this, 1100, 720);
        WindowPlacement.Track(this, "my-provider");

        Panel.CloseRequested += Close;
        Panel.EscapePressed += () =>
        {
            Close();
            return true;
        };
        Closed += (_, _) => Panel.Close();

        Content = root;
        WindowLayout.ApplyChrome(this, root);
        Panel.SetShown(true);
        // Audit M11: minimised or hidden, its 2 s refresh and its reads stop; shown again, they start at once.
        AppWindow.Changed += (sender, change) =>
        {
            if (change.DidPresenterChange || change.DidVisibilityChange)
                Panel.SetShown(sender.IsVisible && sender.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized });
        };
    }

    public ProviderPanel Panel { get; }

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }
}
