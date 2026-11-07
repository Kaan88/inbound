using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI.Text;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One visible line of a container list. Public because the list's bindings read it by reflection.</summary>
public sealed class ContainerListRow : INotifyPropertyChanged
{
    private static readonly string[] AllProperties =
    [
        nameof(Name), nameof(Word), nameof(Detail), nameof(CanCheck), nameof(Indent), nameof(BoxVisibility), nameof(WordVisibility),
        nameof(ProblemVisibility), nameof(DetailVisibility), nameof(NameWeight), nameof(AccessibleName),
    ];

    private readonly ContainerRowList _owner;

    internal ContainerListRow(int index, ContainerRowView view, ContainerRowList owner)
    {
        Index = index;
        View = view;
        _owner = owner;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal int Index { get; }

    internal ContainerRowView View { get; private set; }

    /// <summary>The box showing this row just now. The list reuses them as it scrolls.</summary>
    internal CheckBox? Box { get; set; }

    public string Name => View.Name;

    public string Word => View.Word;

    public string Detail => View.Detail;

    public bool CanCheck => View.CanCheck;

    public Thickness Indent => new(4 + (View.Depth * 18), 0, 8, 0);

    public Visibility BoxVisibility => View.ShowBox ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WordVisibility => View.Word.Length > 0 && !View.IsProblem ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProblemVisibility => View.Word.Length > 0 && View.IsProblem ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DetailVisibility => View.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public FontWeight NameWeight => View.IsHeader ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public string AccessibleName => View.AccessibleName;

    /// <summary>Says what changed and nothing else, so a large list isn't redrawn for one click.</summary>
    internal void Apply(ContainerRowView next)
    {
        var before = View;
        View = next;
        if (Box is { } box && box.IsChecked != next.Checked) _owner.ShowChecked(box, next.Checked);
        if (before with { Checked = next.Checked } == next) return;
        foreach (var name in AllProperties) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => AccessibleName;
}

/// <summary>
/// The rows of a container's preview, or what became of them. Virtualized, because a container can hold
/// 5000 links. It only shows what it is given and says when a box is clicked: the owner decides what that means.
/// </summary>
public sealed partial class ContainerRowList : UserControl
{
    private readonly ObservableCollection<ContainerListRow> _rows = [];
    private bool _locked;
    private bool _showingChecked;

    public ContainerRowList()
    {
        InitializeComponent();
        RowsList.ItemsSource = _rows;
    }

    /// <summary>The row at this place had its box clicked, and is to become this.</summary>
    internal event Action<int, bool>? RowToggled;

    internal string ListName
    {
        set => AutomationProperties.SetName(RowsList, value);
    }

    /// <summary>Stops changes while a choice is on its way, without hiding what was chosen.</summary>
    internal bool IsLocked
    {
        get => _locked;
        set
        {
            _locked = value;
            RowsList.IsEnabled = !value;
        }
    }

    internal void SetRows(IReadOnlyList<ContainerRowView> views)
    {
        _rows.Clear();
        for (var index = 0; index < views.Count; index++) _rows.Add(new ContainerListRow(index, views[index], this));
    }

    /// <summary>The same rows with what is new about them.</summary>
    internal void Apply(IReadOnlyList<ContainerRowView> views)
    {
        if (views.Count != _rows.Count)
        {
            SetRows(views);
            return;
        }

        for (var index = 0; index < views.Count; index++) _rows[index].Apply(views[index]);
    }

    /// <summary>
    /// A box takes its state from here rather than a binding, because a binding doesn't pass on the null
    /// that shows a partly ticked package. A click only ever goes through that third state on its way to
    /// clear, so anything but true means clear.
    /// </summary>
    private void Check_Changed(object sender, RoutedEventArgs e)
    {
        if (_showingChecked || sender is not CheckBox { Tag: ContainerListRow row } box) return;
        if (!_locked) RowToggled?.Invoke(row.Index, box.IsChecked == true);
        ShowChecked(box, row.View.Checked);
    }

    private void Check_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not CheckBox box) return;
        if (box.Tag is ContainerListRow previous && ReferenceEquals(previous.Box, box)) previous.Box = null;
        box.Tag = args.NewValue as ContainerListRow;
        if (box.Tag is not ContainerListRow row) return;
        row.Box = box;
        ShowChecked(box, row.View.Checked);
    }

    internal void ShowChecked(CheckBox box, bool? value)
    {
        _showingChecked = true;
        box.IsChecked = value;
        _showingChecked = false;
    }

    private void RowsList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space) return;

        // A focused row is a container, whose item is its content rather than its data context. A focused box takes Space for itself.
        if (FocusManager.GetFocusedElement(XamlRoot) is not FrameworkElement focused || focused is CheckBox) return;
        if (((focused as ContentControl)?.Content ?? focused.DataContext) is not ContainerListRow row) return;
        if (row.CanCheck && row.View.ShowBox && !_locked) RowToggled?.Invoke(row.Index, row.View.Checked != true);
        e.Handled = true;
    }
}
