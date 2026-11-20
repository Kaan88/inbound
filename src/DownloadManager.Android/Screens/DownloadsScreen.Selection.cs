using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Dialog;

namespace Inbound.App.Screens;

internal sealed partial class DownloadsScreen
{
    private enum SelectionWindow { None, Overflow, Queue, Category, Properties }

    private Bundle? _selectionRestore;
    private PopupWindow? _selectionPopup;
    private Action? _refreshMenuItems;
    private Dialog? _selectionDialog;
    private SelectionWindow _selectionDialogKind;

    internal Bundle? SaveSelection()
    {
        if (_selectionRestore is not null) return _selectionRestore;
        var rows = _list.SelectedRows;
        if (rows.Count == 0) return null;
        var state = new Bundle();
        state.PutStringArray("rows", rows.Select(row => row.Id.ToString()).ToArray());
        state.PutInt("filter", (int)_list.Filter);
        state.PutString("search", _list.Search);
        var window = _selectionDialog?.IsShowing == true ? _selectionDialogKind :
            _selectionPopup?.IsShowing == true ? SelectionWindow.Overflow : SelectionWindow.None;
        state.PutInt("window", (int)window);
        return state;
    }

    private void RestoreSelection()
    {
        var state = _selectionRestore;
        _selectionRestore = null;
        if (state?.GetStringArray("rows") is not { Length: > 0 } saved || !_page.IsShown) return;
        var ids = new HashSet<Guid>();
        foreach (var value in saved)
        {
            if (!Guid.TryParse(value, out var id) || !ids.Add(id)) return;
        }
        var filter = (DownloadFilter)state.GetInt("filter");
        if (_chipFor.TryGetValue(filter, out var chip))
        {
            chip.Checked = true;
            _list.Filter = filter;
        }
        _search.Text = state.GetString("search") ?? "";
        _list.Search = _search.Text;
        if (!_list.RestoreSelection(ids)) return;

        // The window must have an anchor layout. Recheck the selection after the UI has settled.
        _page.Post(() =>
        {
            if (!_page.IsShown || _activity.IsFinishing || _activity.IsDestroyed ||
                !ids.SetEquals(_list.SelectedRows.Select(row => row.Id))) return;
            switch ((SelectionWindow)state.GetInt("window"))
            {
                case SelectionWindow.Overflow: ShowSelectionMenu(_page.FindViewById<View>(Resource.Id.sel_more)!); break;
                case SelectionWindow.Queue: ChooseSelectionQueue(); break;
                case SelectionWindow.Category: ChooseSelectionCategory(); break;
                case SelectionWindow.Properties: ShowSelectionProperties(); break;
            }
        });
    }

    internal void CloseSelectionWindows()
    {
        _refreshMenuItems = null;
        _selectionPopup?.Dismiss();
        _selectionPopup = null;
        _selectionDialog?.Dismiss();
        _selectionDialog = null;
        _selectionDialogKind = SelectionWindow.None;
    }

    private void ShowSelectionDialog(Dialog dialog, SelectionWindow kind)
    {
        _selectionDialog = dialog;
        _selectionDialogKind = kind;
        dialog.Show();
    }

    private void ShowSelectionMenu(View anchor)
    {
        if (!Selecting || _selectionPopup?.IsShowing == true || _selectionDialog?.IsShowing == true) return;

        int dp(int value) => Motion.Dp(_activity, value);
        var content = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        content.SetPadding(0, dp(8), 0, dp(8));
        var background = new GradientDrawable();
        background.SetColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorSurfaceContainerHigh)));
        background.SetCornerRadius(dp(20));
        var popup = new PopupWindow(content, dp(230), ViewGroup.LayoutParams.WrapContent, true)
        {
            Elevation = dp(8),
            OutsideTouchable = true
        };
        popup.SetBackgroundDrawable(background);
        var ripple = new Android.Util.TypedValue();
        _activity.Theme!.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackground, ripple, true);

        View Add(string title, int icon, Action run, bool enabled = true)
        {
            var item = new LinearLayout(_activity)
            {
                Clickable = true, Focusable = true,
                ContentDescription = title, Enabled = enabled, Alpha = enabled ? 1f : .45f
            };
            item.SetGravity(GravityFlags.CenterVertical);
            item.SetPaddingRelative(dp(16), 0, dp(16), 0);
            if (ripple.ResourceId != 0) item.SetBackgroundResource(ripple.ResourceId);
            var image = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
            image.SetImageResource(icon);
            image.SetColorFilter(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
            item.AddView(image, new LinearLayout.LayoutParams(dp(20), dp(20)) { MarginEnd = dp(16) });
            var label = new TextView(_activity) { Text = title, TextSize = 16, ImportantForAccessibility = ImportantForAccessibility.No };
            label.SetTextColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)));
            item.AddView(label);
            item.Click += (_, _) =>
            {
                popup.Dismiss();
                if (Selecting && !_activity.IsFinishing && !_activity.IsDestroyed) run();
            };
            content.AddView(item, new LinearLayout.LayoutParams(-1, dp(48)));
            return item;
        }

        Add("Move to queue", Resource.Drawable.ic_queue, ChooseSelectionQueue,
            !_activity.Capabilities.IsRemote || _model.Hello?.Capabilities.Contains("queueIdentity") == true);
        Add("Change category", Resource.Drawable.ic_flow_folder, ChooseSelectionCategory);
        var again = Add("Download again", Resource.Drawable.ic_refresh,
            () => RunOnSelection(r => _model.RedownloadAsync(r.Id), CanDownloadAgain));
        var copy = Add("Copy address", Resource.Drawable.ic_copy, () =>
        {
            var urls = _list.SelectedRows.Where(r => !r.Job.AddressHidden).Select(r => r.Job.Url);
            var addresses = string.Join("\n", urls);
            if (addresses.Length > 0) Outside.CopyText(_activity, "Address", addresses);
        });
        Add("Properties", Android.Resource.Drawable.IcMenuInfoDetails, ShowSelectionProperties);
        static bool CanDownloadAgain(JobRow row) => row.Group is RowGroup.Finished or RowGroup.Failed;
        static void SetAvailable(View item, bool available)
        {
            item.Enabled = available;
            item.Alpha = available ? 1f : .45f;
        }
        _refreshMenuItems = () =>
        {
            SetAvailable(again, _list.SelectedRows.Any(CanDownloadAgain));
            SetAvailable(copy, _list.SelectedRows.Any(row => !row.Job.AddressHidden));
        };
        _refreshMenuItems();
        popup.DismissEvent += (_, _) =>
        {
            if (!ReferenceEquals(_selectionPopup, popup)) return;
            _refreshMenuItems = null;
            _selectionPopup = null;
        };
        _selectionPopup = popup;
        popup.ShowAsDropDown(anchor, -dp(8), -dp(8), GravityFlags.End);
    }

    private void ChooseSelectionQueue()
    {
        var rows = _list.SelectedRows.ToArray();
        if (rows.Any(row => !row.Job.IsQueued) && _model.Hello?.Capabilities.Contains(JobQueueRequest.UnqueuedPreconditionCapability) != true)
        {
            _activity.ShowMessage("Update the Host before moving downloads that have no queue.");
            return;
        }
        var queues = _model.Settings.Queues.OrderBy(q => q.Order).ThenBy(q => q.Id).ToArray();
        var sources = rows.Select(row => row.Job.IsQueued ? (int?)row.Job.QueueId : null).ToArray();
        var sourceGenerations = sources.Select(id => queues.FirstOrDefault(q => q.Id == id)?.Generation).ToArray();
        if (sources.Where((id, i) => id is not null && sourceGenerations[i].GetValueOrDefault() == Guid.Empty).Any())
        {
            _activity.ShowMessage("Queue identity is unavailable. Refresh the Host and try again.");
            return;
        }

        var targets = new QueueDto?[] { null }.Concat(queues.Where(q => q.Generation.GetValueOrDefault() != Guid.Empty)).ToArray();
        var names = targets.Select(q => q?.Name ?? "No queue").ToArray();
        ChooseForSelection(SelectionWindow.Queue, "Move to queue", names, (index, choice) =>
        {
            var target = targets[choice];
            return _model.MoveToQueueAsync(rows[index].Id, target?.Id,
                expectedQueueId: sources[index], expectedSourceQueueGeneration: sourceGenerations[index],
                expectedDestinationQueueGeneration: target?.Generation, expectedUnqueued: sources[index] is null);
        }, rows);
    }

    private void ChooseSelectionCategory()
    {
        var rows = _list.SelectedRows.ToArray();
        var choices = new[] { (Id: "", Label: "Automatic") }
            .Concat(FileCategories.Names.Append("other").Select(id => (Id: id, Label: char.ToUpperInvariant(id[0]) + id[1..])))
            .Concat(_model.Settings.CustomCategories.Select(category => (Id: category.Id, Label: category.Name)))
            .ToArray();
        ChooseForSelection(SelectionWindow.Category, "Change category", choices.Select(c => c.Label).ToArray(),
            (index, choice) => _model.ChangeCategoryAsync(rows[index].Id, choices[choice].Id), rows);
    }

    private void ChooseForSelection(SelectionWindow kind, string title, string[] choices, Func<int, int, Task<CommandResult>> apply, JobRow[] rows)
    {
        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle(title)!
            .SetItems(choices, async (_, e) =>
            {
                if (_activity.IsFinishing || _activity.IsDestroyed) return;
                if (_list.SelectedRows.Count != rows.Length || rows.Any(row => !_list.IsSelected(row)))
                {
                    _activity.ShowMessage("The selection changed. Choose the downloads again.", downloadsFeedback: true);
                    return;
                }

                var choice = e.Which;
                _list.ClearSelection();
                int count = 0;
                foreach (var row in rows)
                {
                    var result = await apply(count, choice);
                    if (!result.Ok)
                    {
                        _activity.ShowMessage($"{count} of {rows.Length} updated. {row.Name}: {result.Error ?? "The Host refused."}",
                            downloadsFeedback: true);
                        return;
                    }
                    count++;
                }
                _activity.ShowMessage($"{count} {(count == 1 ? "download" : "downloads")} updated.", downloadsFeedback: true);
            })!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!.Create()!;
        ShowSelectionDialog(dialog, kind);
    }

    private void ShowSelectionProperties()
    {
        var rows = _list.SelectedRows.ToArray();
        if (rows.Length == 1)
        {
            OpenProperties(rows[0]);
            return;
        }

        var dialog = new MaterialAlertDialogBuilder(_activity).SetTitle("Properties")!
            .SetItems(rows.Select(row => row.Name).ToArray(), (_, e) => OpenProperties(rows[e.Which]))!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!.Create()!;
        ShowSelectionDialog(dialog, SelectionWindow.Properties);
    }

    private void OpenProperties(JobRow row)
    {
        if (!_list.IsSelected(row)) return;
        _list.ClearSelection();
        _activity.OpenDetails(row, CardOf(row));
    }
}
