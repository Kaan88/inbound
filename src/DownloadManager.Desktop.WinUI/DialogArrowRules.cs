namespace DownloadManager.Desktop.WinUI;

/// <summary>An arrow key pressed in a dialog.</summary>
internal enum DialogArrow
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// Arrow keys between a dialog's buttons (UI.R36). The default button stays as it is (Cancel for a delete); the arrows
/// move the focus along the dialog's buttons, and Enter then presses the focused one. Nothing here touches the UI.
/// </summary>
internal static class DialogArrowRules
{
    /// <summary>
    /// Where the focus goes from button <paramref name="current"/> of <paramref name="count"/>, in reading order (the
    /// check boxes and buttons of the dialog's text first, in the order they stand, then the footer buttons left to
    /// right). Left and Up go back, Right and Down go on; at either end the focus stays. Null when the key does nothing
    /// here, so the key is left to the rest of the dialog. In a right-to-left layout Left and Right swap, as the
    /// buttons do. An arrow pressed with Shift, Ctrl, Alt or Windows (<paramref name="modified"/>) does nothing, as in
    /// the web UI.
    /// </summary>
    public static int? Target(int count, int current, DialogArrow arrow, bool rightToLeft = false, bool modified = false)
    {
        if (modified || count < 2 || current < 0 || current >= count) return null;
        var back = arrow switch
        {
            DialogArrow.Left => !rightToLeft,
            DialogArrow.Right => rightToLeft,
            DialogArrow.Up => true,
            _ => false,
        };
        var next = back ? current - 1 : current + 1;
        return next < 0 || next >= count ? null : next;
    }

    /// <summary>The footer buttons' names in a ContentDialog's template, in the order they stand.</summary>
    public static readonly string[] FooterButtonNames = ["PrimaryButton", "SecondaryButton", "CloseButton"];
}
