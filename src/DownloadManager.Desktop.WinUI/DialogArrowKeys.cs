using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R36: the arrow keys move the focus between a ContentDialog's buttons, which WinUI's dialog does not do itself, so
/// Enter then presses the focused one. The default button is left as each dialog sets it. Only a key pressed while a
/// footer button or one of the buttons or check boxes of the dialog's text has the focus moves it (File > Servers has
/// its Add, Edit, Remove and Show buttons in its text): a text box, list, combo box or radio group in the dialog keeps
/// its arrow keys. An arrow with Shift, Ctrl, Alt or Windows is left alone, as in the web UI.
/// </summary>
internal static class DialogArrowKeys
{
    // Dialogs that already have the handler; one dialog can be shown more than once.
    private static readonly ConditionalWeakTable<ContentDialog, object> Attached = new();

    /// <summary>Gives <paramref name="dialog"/> arrow keys between its buttons. Safe to call more than once.</summary>
    public static void Add(ContentDialog dialog)
    {
        if (Attached.TryGetValue(dialog, out _)) return;
        Attached.Add(dialog, new object());
        // Handled ones too: the dialog's scroll viewer takes Up and Down from a check box in its text to scroll.
        dialog.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
    }

    private static void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ContentDialog dialog) return;
        DialogArrow? arrow = e.Key switch
        {
            VirtualKey.Left => DialogArrow.Left,
            VirtualKey.Right => DialogArrow.Right,
            VirtualKey.Up => DialogArrow.Up,
            VirtualKey.Down => DialogArrow.Down,
            _ => null,
        };
        if (arrow is null || e.OriginalSource is not ButtonBase focused) return;

        var stops = Stops(dialog);
        var index = stops.IndexOf(focused);
        if (index < 0) return;
        if (DialogArrowRules.Target(stops.Count, index, arrow.Value, dialog.FlowDirection == FlowDirection.RightToLeft, AnyModifierDown()) is not { } target) return;
        if (stops[target].Focus(FocusState.Keyboard)) e.Handled = true;
    }

    private static bool AnyModifierDown() =>
        IsDown(VirtualKey.Shift) || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu)
        || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>The buttons and check boxes of the dialog's text, then its footer buttons, as they stand; only the shown and enabled ones.</summary>
    private static List<Control> Stops(ContentDialog dialog)
    {
        var stops = new List<Control>();
        if (dialog.Content is UIElement content) AddContentStops(content, stops);
        var footer = new List<Button>();
        FindFooterButtons(dialog, dialog.Content as DependencyObject, footer, depth: 20);
        stops.AddRange(DialogArrowRules.FooterButtonNames
            .Select(name => footer.FirstOrDefault(button => button.Name == name))
            .OfType<Button>()
            .Where(IsStop));
        return stops;
    }

    private static bool IsStop(Control control) =>
        control.Visibility == Visibility.Visible && control.IsEnabled && control.IsTabStop;

    // A list, combo box or item repeater keeps its arrows, and so do the buttons and check boxes in it. Other controls
    // are not entered either, so the clear button in a text box's template is never a stop; panels, borders, scroll
    // viewers and plain content holders are.
    private static void AddContentStops(DependencyObject element, List<Control> stops)
    {
        if (element is UIElement { Visibility: not Visibility.Visible }) return;
        if (element is ItemsControl or ItemsRepeater) return;
        if (element is CheckBox or Button)
        {
            var control = (Control)element;
            if (IsStop(control)) stops.Add(control);
            return;
        }

        if (element is Control && element is not (ScrollViewer or UserControl or Expander)
            && element.GetType() != typeof(ContentControl)) return;
        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++) AddContentStops(VisualTreeHelper.GetChild(element, i), stops);
    }

    // The footer is in the dialog's template, outside its text, which is not walked.
    private static void FindFooterButtons(DependencyObject element, DependencyObject? content, List<Button> found, int depth)
    {
        if (content is not null && ReferenceEquals(element, content)) return;
        if (element is Button { Name: "PrimaryButton" or "SecondaryButton" or "CloseButton" } button)
        {
            found.Add(button);
            return;
        }

        if (depth == 0) return;
        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++) FindFooterButtons(VisualTreeHelper.GetChild(element, i), content, found, depth - 1);
    }
}
