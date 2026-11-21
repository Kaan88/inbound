using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.TorBox;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;

namespace Inbound.App.Screens;

/// <summary>
/// TorBox (t5): the account's jobs as cards with the chips All, Ready, On TorBox and Failed. The Host's remote.list gives the first list
/// and remote.changed pushes keep it. Get opens the file choice and adds the files to Downloads; a card opens its files; a long press
/// offers Delete from TorBox.
/// </summary>
internal sealed class TorBoxScreen
{
    private readonly MainActivity _activity;
    private readonly DownloadsModel _host;
    private readonly TorBoxClient _client;
    private readonly TorBoxList _list = new();
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly LinearLayout _cards;
    private readonly TextView _error, _empty;
    private readonly EditText _search;
    private readonly Dictionary<TorBoxChip, Chip> _chips = new();
    private readonly HashSet<string> _described = new(StringComparer.Ordinal);
    private TorBoxChip _chip = TorBoxChip.All;
    private Java.Lang.IRunnable? _renderLater;
    private Android.App.Dialog? _dialog;
    private bool _loaded, _disposed;

    public TorBoxScreen(MainActivity activity, DownloadsModel host, TorBoxClient client)
    {
        _activity = activity;
        _host = host;
        _client = client;
        Root = LayoutInflater.From(activity)!.Inflate(Resource.Layout.torbox_page, null, false)!;
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.torbox_top)!, top: true, bottom: false);
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.torbox_root)!, top: false, bottom: true);
        _cards = Root.FindViewById<LinearLayout>(Resource.Id.torbox_list)!;
        _error = Root.FindViewById<TextView>(Resource.Id.torbox_error)!;
        _empty = Root.FindViewById<TextView>(Resource.Id.torbox_empty)!;
        _search = Root.FindViewById<EditText>(Resource.Id.torbox_search_input)!;
        Root.FindViewById<TextView>(Resource.Id.torbox_hint)!.Text = TorBoxText.FootHint;
        Root.FindViewById<View>(Resource.Id.torbox_back)!.Click += (_, _) => _activity.CloseRowPage(Root);
        Root.FindViewById<View>(Resource.Id.torbox_search)!.Click += (_, _) => ToggleSearch();
        Root.FindViewById<View>(Resource.Id.torbox_more)!.Click += (_, e) => MoreMenu(Root.FindViewById<View>(Resource.Id.torbox_more)!);
        _search.TextChanged += (_, _) => Render();
        var group = Root.FindViewById<ViewGroup>(Resource.Id.torbox_chips)!;
        foreach (var chip in Enum.GetValues<TorBoxChip>())
        {
            var view = (Chip)LayoutInflater.From(activity)!.Inflate(Resource.Layout.chip_filter, group, false)!;
            var which = chip;
            view.Checked = chip == TorBoxChip.All;
            view.Click += (_, _) => { _chip = which; Render(); };
            group.AddView(view);
            _chips[chip] = view;
        }

        host.RemoteChanged += OnChanged;
        Load();
        Render();
    }

    public View Root { get; }

    public void Dispose()
    {
        _disposed = true;
        _host.RemoteChanged -= OnChanged;
        if (_renderLater is not null) _ui.RemoveCallbacks(_renderLater);
        _dialog?.Dismiss();
    }

    private string Where => _activity.Capabilities.IsRemote ? "on " + _activity.HostName : "on this phone";

    private void Load()
    {
        _error.Visibility = ViewStates.Gone;
        TorBoxUi.Run(_activity, () => _client.ListAsync(), result =>
        {
            if (_disposed) return;
            if (!result.Ok) { Fail(TorBoxText.Failure(result.Error)); return; }
            _loaded = true;
            _list.Load(result.Jobs);
            Render();
            Describe();
        }, Fail);
    }

    /// <summary>The account's items carry the sizes and file counts a job does not; asked once per item seen, best effort.</summary>
    private void Describe()
    {
        foreach (var kind in _list.KindsToDescribe())
        {
            if (!_described.Add(kind + "/" + _list.Jobs.Count)) continue;
            TorBoxUi.Run(_activity, () => _client.ItemsAsync(kind), result =>
            {
                if (_disposed || !result.Ok) return;
                _list.SetItems(result.Items);
                Render();
            }, _ => { });
        }
    }

    private void Fail(string words)
    {
        if (_disposed) return;
        _error.Text = words;
        _error.Visibility = ViewStates.Visible;
        Render();
    }

    private void OnChanged(RemoteChangedDto change)
    {
        if (_disposed || !_list.Apply(change)) return;
        _loaded = true;
        // A push can come several times a second while TorBox fetches: the cards are drawn at most four times a second.
        if (_renderLater is not null) return;
        _renderLater = new Java.Lang.Runnable(() => { _renderLater = null; Render(); Describe(); });
        _ui.PostDelayed(_renderLater, 250);
    }

    private void ToggleSearch()
    {
        var opening = _search.Visibility != ViewStates.Visible;
        var keyboard = (Android.Views.InputMethods.InputMethodManager?)_activity.GetSystemService(Android.Content.Context.InputMethodService);
        if (opening)
        {
            _search.Visibility = ViewStates.Visible;
            if (_search.RequestFocus()) _search.Post(() => keyboard?.ShowSoftInput(_search, 0));
        }
        else
        {
            keyboard?.HideSoftInputFromWindow(_search.WindowToken, 0);
            _search.Text = "";
            _search.Visibility = ViewStates.Gone;
        }
    }

    private void MoreMenu(View anchor)
    {
        var menu = new PopupMenu(_activity, anchor);
        menu.Menu!.Add(0, 1, 0, "Refresh");
        menu.Menu!.Add(0, 2, 1, "Ask TorBox now");
        menu.MenuItemClick += (_, e) =>
        {
            if (e.Item!.ItemId == 1) Load();
            else TorBoxUi.Run(_activity, () => _client.CheckNowAsync(), r =>
            {
                if (!r.Ok) Fail(TorBoxText.Failure(r.Error));
            }, Fail);
        };
        menu.Show();
    }

    private void Render()
    {
        if (_disposed) return;
        var all = _list.Rows(Where);
        foreach (var (chip, view) in _chips) view.Text = TorBoxRows.ChipLabel(all, chip);
        var rows = TorBoxRows.Filter(all, _chip, _search.Text);
        _cards.RemoveAllViews();
        foreach (var row in rows) _cards.AddView(Card(row));
        _empty.Visibility = rows.Count == 0 && _loaded ? ViewStates.Visible : ViewStates.Gone;
        _empty.Text = all.Count == 0 ? "Nothing on TorBox yet. Send a magnet from Add torrent." : "Nothing here.";
    }

    private View Card(TorBoxRow row)
    {
        var card = LayoutInflater.From(_activity)!.Inflate(Resource.Layout.torbox_row, _cards, false)!;
        var icon = card.FindViewById<ImageView>(Resource.Id.icon)!;
        icon.SetImageResource(row.IsTorrent ? Resource.Drawable.ic_add_source_torrent : Resource.Drawable.ic_file);
        icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(_activity.GetColor(
            row.Tone == TorBoxTone.Failed ? Resource.Color.state_failed : Resource.Color.state_media)));
        card.FindViewById<TextView>(Resource.Id.name)!.Text = row.Name;
        var sub = card.FindViewById<TextView>(Resource.Id.subtitle)!;
        var word = _activity.GetColor(row.Tone switch
        {
            TorBoxTone.Done => Resource.Color.state_done, TorBoxTone.Waiting => Resource.Color.state_waiting,
            TorBoxTone.Failed => Resource.Color.state_failed, _ => Resource.Color.state_done,
        });
        var line = new SpannableString(row.Line);
        line.SetSpan(new ForegroundColorSpan(row.Tone == TorBoxTone.Active ? new Color(Motion.Attr(_activity, Resource.Attribute.colorPrimary)) : new Color(word)),
            0, row.Word.Length, SpanTypes.ExclusiveExclusive);
        if (row.Action == TorBoxRowAction.OnPhone)
        {
            var at = row.Line.LastIndexOf(" · ", StringComparison.Ordinal) + 3;
            line.SetSpan(new ForegroundColorSpan(new Color(_activity.GetColor(Resource.Color.state_done))), at, row.Line.Length, SpanTypes.ExclusiveExclusive);
        }

        sub.SetText(line, TextView.BufferType.Spannable);
        card.ContentDescription = row.Name + ". " + row.Line;

        var bar = card.FindViewById<LinearProgressIndicator>(Resource.Id.bar)!;
        bar.Visibility = row.Progress is null ? ViewStates.Gone : ViewStates.Visible;
        if (row.Progress is { } p)
        {
            bar.SetIndicatorColor(_activity.GetColor(Resource.Color.state_media));
            bar.Progress = (int)(p * 1000);
        }

        var get = card.FindViewById<MaterialButton>(Resource.Id.get)!;
        var action = card.FindViewById<ImageButton>(Resource.Id.action)!;
        get.Visibility = row.Action == TorBoxRowAction.Get ? ViewStates.Visible : ViewStates.Gone;
        get.ContentDescription = "Get files of " + row.Name;
        get.Click += (_, _) => OpenFiles(row, true);
        action.Visibility = row.Action is TorBoxRowAction.OnPhone or TorBoxRowAction.Delete ? ViewStates.Visible : ViewStates.Gone;
        if (row.Action == TorBoxRowAction.OnPhone)
        {
            action.SetImageResource(Resource.Drawable.ic_chevron_right);
            action.ContentDescription = "Show " + row.Name + " in Downloads";
            action.Click += (_, _) => { _activity.CloseRowPage(Root); _activity.ShowDownloadsTab(); };
        }
        else if (row.Action == TorBoxRowAction.Delete)
        {
            action.SetImageResource(Resource.Drawable.ic_remote_trash);
            action.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)));
            action.ContentDescription = "Delete " + row.Name;
            action.Click += (_, _) => ConfirmDelete(row);
        }

        card.Click += (_, _) => { if (row.Group != TorBoxGroup.Failed) OpenFiles(row, row.Action == TorBoxRowAction.Get); };
        card.LongClick += (_, e) => { ConfirmDelete(row, fromMenu: true); e.Handled = true; };
        return card;
    }

    private void OpenFiles(TorBoxRow row, bool choose)
    {
        if (_disposed) return;
        _dialog?.Dismiss();
        _dialog = TorBoxFilesSheet.Show(_activity, _client, row, choose, () => _activity.ShowMessage("Added to Downloads from TorBox"));
    }

    private void ConfirmDelete(TorBoxRow row, bool fromMenu = false)
    {
        if (_disposed) return;
        var builder = new MaterialAlertDialogBuilder(_activity).SetTitle(fromMenu ? row.Name : "Remove " + row.Name + "?")!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!;
        var message = row.HasItem
            ? "Delete from TorBox removes it from your TorBox account for good. Files already added to Downloads stay."
            : "";
        builder.SetMessage((message + (message.Length > 0 ? "\n\n" : "") + "Remove from this list only takes it off here; it stays on TorBox.").Trim());
        if (row.HasItem)
            builder.SetPositiveButton("Delete from TorBox", (_, _) => Run(() => _client.DeleteFromTorBoxAsync(row.Id), row.Id));
        builder.SetNeutralButton("Remove from list", (_, _) => Run(() => _client.RemoveAsync(row.Id), row.Id, listOnly: true));
        _dialog = builder.Show();
    }

    private void Run(Func<Task<RemoteActionResultDto>> work, Guid id, bool listOnly = false) =>
        TorBoxUi.Run(_activity, work, result =>
        {
            if (_disposed) return;
            if (!result.Ok) { Fail(TorBoxText.Failure(result.Error)); return; }
            // The Host also pushes the change; this makes the card go even if that push is late.
            _list.Apply(new RemoteChangedDto { Change = "removed", JobId = id });
            Render();
            _activity.ShowMessage(listOnly ? "Removed from the list" : "Deleted from TorBox");
        }, Fail);
}
