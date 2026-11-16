using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.11 R2a: the row under a pane's header (sites-rework.md, `browse`): an Up button, the path as clickable parts in a
/// box, a Star button, and, in Server only, a Filter box. Used by the server pane and the This PC pane so the two read as a
/// pair. A pane with <see cref="Editable"/> turns the box into a text box when its empty part is clicked.
/// </summary>
internal sealed class PathBar : UserControl
{
    private readonly Button _up = new();
    private readonly Button _star = new();
    private readonly FontIcon _starIcon = new() { FontSize = 14 };
    private readonly Border _box = new();
    private readonly ScrollViewer _crumbScroll = new();
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBox _edit = new();
    private readonly Grid _boxContent = new();
    private readonly TextBox _filter = new() { PlaceholderText = "Filter", MinHeight = 0, Padding = new Thickness(8, 4, 8, 4), Width = 150, FontSize = 12 };
    private readonly Grid _root = new() { ColumnSpacing = 8, Padding = new Thickness(8, 6, 8, 6) };
    private bool _editing;
    private bool _starred;

    public PathBar()
    {
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _up.Content = new FontIcon { Glyph = "", FontSize = 12 };
        _up.Padding = new Thickness(8, 5, 8, 5);
        _up.MinWidth = 32;
        AutomationProperties.SetName(_up, "Folder above");
        ToolTipService.SetToolTip(_up, "The folder above (Backspace)");
        _up.Click += (_, _) => UpClicked?.Invoke();
        Grid.SetColumn(_up, 0);

        _crumbScroll.HorizontalScrollMode = ScrollMode.Enabled;
        _crumbScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _crumbScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _crumbScroll.VerticalAlignment = VerticalAlignment.Center;
        _crumbScroll.Content = _crumbs;
        AutomationProperties.SetName(_crumbs, "Path");
        _boxContent.Children.Add(_crumbScroll);

        _edit.Visibility = Visibility.Collapsed;
        _edit.FontSize = 12;
        _edit.MinHeight = 0;
        _edit.Padding = new Thickness(8, 4, 8, 4);
        _edit.BorderThickness = new Thickness(0);
        _edit.KeyDown += Edit_KeyDown;
        _edit.LostFocus += (_, _) => EndEdit();
        AutomationProperties.SetName(_edit, "Folder, type a path and press Enter");
        _boxContent.Children.Add(_edit);

        _box.CornerRadius = new CornerRadius(4);
        _box.BorderThickness = new Thickness(1);
        _box.MinHeight = 32;
        _box.Child = _boxContent;
        // Brushes set in code follow the window's theme (AppTheme sets the root's RequestedTheme) and its switches.
        InboundUi.Follow(_box, "InboundChromeBrush", brush => _box.Background = brush);
        InboundUi.Follow(_box, "InboundDividerBrush", brush => _box.BorderBrush = brush);
        _box.Tapped += Box_Tapped;
        Grid.SetColumn(_box, 1);

        _star.Content = _starIcon;
        _star.Padding = new Thickness(8, 5, 8, 5);
        _star.MinWidth = 32;
        _star.Click += (_, _) => StarClicked?.Invoke();
        _star.ContextFlyout = StarFlyout;
        InboundUi.Follow(_starIcon, "InboundIconPauseBrush", brush =>
        {
            if (_starred) _starIcon.Foreground = brush;
        });
        Starred = false;
        Grid.SetColumn(_star, 2);

        _filter.TextChanged += (_, _) => FilterChanged?.Invoke(_filter.Text);
        _filter.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_filter, "Filter this folder's names");
        ToolTipService.SetToolTip(_filter, "Show only names that contain this text (Ctrl+F)");
        Grid.SetColumn(_filter, 3);

        _root.Children.Add(_up);
        _root.Children.Add(_box);
        _root.Children.Add(_star);
        _root.Children.Add(_filter);
        Content = _root;
    }

    /// <summary>A part of the path was clicked: its key (a remote or local path).</summary>
    internal event Action<string>? CrumbClicked;

    internal event Action? UpClicked;

    internal event Action? StarClicked;

    /// <summary>Enter in the edit box: the typed text.</summary>
    internal event Action<string>? PathSubmitted;

    /// <summary>
    /// Enter or Escape ended the edit. Raised while the text box still has the keyboard, so the pane can take it before
    /// the box is hidden (a hidden focused box hands the keyboard to the menu bar).
    /// </summary>
    internal event Action? EditEnded;

    internal event Action<string>? FilterChanged;

    /// <summary>The starred folders, shown by a right click on the Star button.</summary>
    internal MenuFlyout StarFlyout { get; } = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

    internal bool Editable { get; set; }

    internal bool IsEditing => _editing;

    internal bool CanGoUp { set => _up.IsEnabled = value; }

    internal bool StarEnabled { set => _star.IsEnabled = value; }

    internal bool Starred
    {
        set
        {
            _starred = value;
            _starIcon.Glyph = value ? "" : "";
            if (value) _starIcon.Foreground = InboundTheme.Brush("InboundIconPauseBrush", _starIcon);
            else _starIcon.ClearValue(IconElement.ForegroundProperty);
            AutomationProperties.SetName(_star, value ? "Unstar this folder" : "Star this folder");
            ToolTipService.SetToolTip(_star, value ? "Remove the star from this folder (right-click: starred folders)" : "Star this folder (right-click: starred folders)");
        }
    }

    internal bool ShowFilter
    {
        set
        {
            _filter.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value && _filter.Text.Length > 0) _filter.Text = string.Empty;
        }
    }

    internal string FilterText => _filter.Text;

    internal bool FilterVisible => _filter.Visibility == Visibility.Visible;

    internal void FocusFilter()
    {
        if (!FilterVisible) return;
        _filter.Focus(FocusState.Programmatic);
        _filter.SelectAll();
    }

    /// <summary>The path as parts; the last one is the folder shown (bold, not a button).</summary>
    internal void SetCrumbs(IReadOnlyList<(string Text, string Key)> crumbs)
    {
        _crumbs.Children.Clear();
        for (var index = 0; index < crumbs.Count; index++)
        {
            var (text, key) = crumbs[index];
            if (index > 0)
            {
                var separator = new FontIcon { Glyph = "", FontSize = 9, VerticalAlignment = VerticalAlignment.Center };
                InboundUi.Follow(separator, "InboundSecondaryTextBrush", brush => separator.Foreground = brush);
                _crumbs.Children.Add(separator);
            }

            if (index == crumbs.Count - 1)
            {
                var current = new TextBlock
                {
                    Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0),
                };
                AutomationProperties.SetName(current, $"Current folder: {text}");
                _crumbs.Children.Add(current);
                continue;
            }

            var button = new Button
            {
                Content = text, FontSize = 12, Padding = new Thickness(6, 3, 6, 3), Tag = key, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0), MinHeight = 0,
            };
            AutomationProperties.SetName(button, $"Open folder {text}");
            button.Click += (_, _) => CrumbClicked?.Invoke(key);
            _crumbs.Children.Add(button);
        }

        _crumbScroll.UpdateLayout();
        _crumbScroll.ChangeView(_crumbScroll.ScrollableWidth, null, null, disableAnimation: true);
    }

    /// <summary>Turns the box into a text box holding <paramref name="text"/> (Ctrl+L, or a click on the empty part).</summary>
    internal void BeginEdit(string text)
    {
        if (!Editable) return;
        _editing = true;
        _crumbScroll.Visibility = Visibility.Collapsed;
        _edit.Visibility = Visibility.Visible;
        _edit.Text = text;
        _edit.Focus(FocusState.Programmatic);
        _edit.SelectAll();
    }

    internal void EndEdit()
    {
        if (!_editing) return;
        _editing = false;
        _edit.Visibility = Visibility.Collapsed;
        _crumbScroll.Visibility = Visibility.Visible;
    }

    private void Edit_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var typed = _edit.Text;
            PathSubmitted?.Invoke(typed);
            EditEnded?.Invoke();
            EndEdit();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EditEnded?.Invoke();
            EndEdit();
        }
    }

    private void Box_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!Editable || _editing) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return;
        }

        e.Handled = true;
        EditRequested?.Invoke();
    }

    /// <summary>The empty part of the box was clicked: the pane says what to put in the edit box.</summary>
    internal event Action? EditRequested;
}
