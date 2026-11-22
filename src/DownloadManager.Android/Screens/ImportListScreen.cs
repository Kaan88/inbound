using Android.Views;
using Android.Widget;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Lists;
using DownloadManager.Mobile.Torrents;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>
/// Import a list (i1, i2): what an exported list holds (counts and the first few names), what can't be added, where files go, and Add.
/// Only drawing and the two pickers; what is sent and how lives in <see cref="ListImportModel"/>.
/// </summary>
internal sealed class ImportListScreen
{
    private readonly MainActivity _activity;
    private readonly DownloadsModel _host;
    private readonly TorrentClient? _torrents;
    private readonly TextInputEditText _file, _saveTo;
    private readonly TextView _downloadsN, _linksN, _torrentsN, _refusedTitle, _refusedBody, _saveHint;
    private readonly View _refusedCard;
    private readonly LinearLayout _rows;
    private readonly MaterialButton _send;
    private readonly TextInputLayout _saveLayout;
    private readonly CancellationTokenSource _life = new();
    private ListImportModel _model;
    private bool _disposed, _sending;

    /// <param name="torrents">Null when the Host doesn't answer torrent.*: the list's torrents are then skipped with that reason.</param>
    public ImportListScreen(MainActivity activity, DownloadsModel host, TorrentClient? torrents, ListImportModel model)
    {
        _activity = activity;
        _host = host;
        _torrents = torrents;
        _model = model;
        Root = LayoutInflater.From(activity)!.Inflate(Resource.Layout.import_list, null, false)!;
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.import_top)!, top: true, bottom: false);
        InsetsListener.Pad(Root.FindViewById<View>(Resource.Id.import_bottom)!, top: false, bottom: true);
        _file = Root.FindViewById<TextInputEditText>(Resource.Id.import_file)!;
        _downloadsN = Root.FindViewById<TextView>(Resource.Id.tile_downloads_n)!;
        _linksN = Root.FindViewById<TextView>(Resource.Id.tile_links_n)!;
        _torrentsN = Root.FindViewById<TextView>(Resource.Id.tile_torrents_n)!;
        _rows = Root.FindViewById<LinearLayout>(Resource.Id.import_rows)!;
        _refusedCard = Root.FindViewById<MaterialCardView>(Resource.Id.import_refused_card)!;
        _refusedTitle = Root.FindViewById<TextView>(Resource.Id.import_refused_title)!;
        _refusedBody = Root.FindViewById<TextView>(Resource.Id.import_refused_body)!;
        _saveTo = Root.FindViewById<TextInputEditText>(Resource.Id.import_save_to)!;
        _saveLayout = Root.FindViewById<TextInputLayout>(Resource.Id.import_save_layout)!;
        _saveHint = Root.FindViewById<TextView>(Resource.Id.import_save_hint)!;
        _send = Root.FindViewById<MaterialButton>(Resource.Id.import_send)!;

        Root.FindViewById<View>(Resource.Id.import_close)!.Click += (_, _) => _activity.CloseRowPage(Root);
        Root.FindViewById<View>(Resource.Id.import_cancel)!.Click += (_, _) => _activity.CloseRowPage(Root);
        Root.FindViewById<TextInputLayout>(Resource.Id.import_file_layout)!.SetEndIconOnClickListener(new ClickListener(() => _activity.ChooseImportList(this)));
        _send.Click += (_, _) => Send();
        _saveTo.Click += (_, _) => ChooseFolder();
        _saveLayout.SetEndIconOnClickListener(new ClickListener(ChooseFolder));
        _host.SettingsChanged += ShowSaveTo;
        Render();
    }

    public View Root { get; }

    public void Dispose()
    {
        _disposed = true;
        _life.Cancel();
        _host.SettingsChanged -= ShowSaveTo;
    }

    /// <summary>Another file was chosen: the page shows it (the folder choice is kept).</summary>
    public void Load(ListImportModel model)
    {
        if (_disposed || _sending) return;
        model.ChosenFolder = _model.ChosenFolder;
        _model = model;
        Render();
    }

    private void Render()
    {
        var m = _model;
        _file.Text = m.FileName;
        _file.ContentDescription = "List file " + m.FileName;
        _downloadsN.Text = m.Downloads.ToString();
        _linksN.Text = m.Links.ToString();
        _torrentsN.Text = m.Torrents.ToString();
        RenderRows(m);
        _refusedCard.Visibility = m.RefusedTitle is null ? ViewStates.Gone : ViewStates.Visible;
        _refusedTitle.Text = m.RefusedTitle;
        _refusedBody.Text = m.RefusedBody;
        _saveLayout.Hint = m.SaveToLabel;
        _saveHint.Text = m.SaveToHint;
        ShowSaveTo();
        UpdateSend();
    }

    private void RenderRows(ListImportModel m)
    {
        _rows.RemoveAllViews();
        var dp = (int d) => Motion.Dp(_activity, d);
        var muted = Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant);
        var text = Motion.Attr(_activity, Resource.Attribute.colorOnSurface);
        foreach (var row in m.Preview)
        {
            var line = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            line.SetGravity(GravityFlags.CenterVertical);
            line.SetMinimumHeight(dp(44));
            line.SetPadding(dp(10), 0, dp(14), 0);
            var icon = new ImageView(_activity);
            icon.SetImageResource(row.IsTorrent ? Resource.Drawable.ic_torrent : Resource.Drawable.ic_link);
            icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(row.IsTorrent ? new Android.Graphics.Color(_activity.GetColor(Resource.Color.state_done)) : new Android.Graphics.Color(muted));
            icon.ImportantForAccessibility = ImportantForAccessibility.No;
            line.AddView(icon, new LinearLayout.LayoutParams(dp(20), dp(20)));
            var name = new TextView(_activity) { Text = row.Name };
            name.SetSingleLine(true);
            name.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            name.SetTextSize(Android.Util.ComplexUnitType.Sp, 14.5f);
            name.SetTextColor(new Android.Graphics.Color(text));
            var nameParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = dp(10) };
            line.AddView(name, nameParams);
            line.ContentDescription = (row.IsTorrent ? "Torrent. " : "Link. ") + row.Name;
            _rows.AddView(line);
        }

        if (m.MoreText is { } more)
        {
            var tv = new TextView(_activity) { Text = more };
            tv.SetTextSize(Android.Util.ComplexUnitType.Sp, 14.5f);
            tv.SetTextColor(new Android.Graphics.Color(muted));
            tv.SetMinimumHeight(dp(44));
            tv.Gravity = GravityFlags.CenterVertical;
            tv.SetPadding(dp(40), 0, dp(14), 0);
            _rows.AddView(tv);
        }
    }

    private void ShowSaveTo()
    {
        if (_disposed) return;
        var full = _model.SaveToValue(_host.DefaultDownloadDirectory);
        void Fit()
        {
            var room = _saveTo.Width - _saveTo.CompoundPaddingLeft - _saveTo.CompoundPaddingRight;
            var cut = room <= 0 ? full : MiddleEllipsis.Cut(full, s => _saveTo.Paint!.MeasureText(s) <= room);
            if (_saveTo.Text != cut) _saveTo.Text = cut;
        }

        _saveTo.Text = full;
        _saveTo.ContentDescription = _model.SaveToLabel + ", " + full;
        var offered = FolderPicker.Offered(_host) && !_sending;
        _saveTo.Enabled = offered; _saveTo.Focusable = false; _saveTo.Clickable = offered; _saveTo.LongClickable = false;
        _saveLayout.EndIconContentDescription = offered ? "Choose a folder on " + _activity.HostName : null;
        _saveTo.Post(Fit);
    }

    private void ChooseFolder()
    {
        if (_disposed || _sending || !FolderPicker.Offered(_host)) return;
        var start = !string.IsNullOrWhiteSpace(_model.ChosenFolder) ? _model.ChosenFolder : _host.DefaultDownloadDirectory;
        FolderSheet.Show(_activity, _host, start, folder => { _model.ChosenFolder = folder; ShowSaveTo(); }, _life.Token);
    }

    private void UpdateSend()
    {
        _send.Text = _model.AddLabel;
        _send.Enabled = _model.CanSend && !_sending;
    }

    private void Send()
    {
        if (_disposed || _sending || !_model.CanSend) return;
        _sending = true;
        var model = _model;
        var torrents = _torrents;
        var host = _host;
        var activity = _activity;
        var page = Root;
        // The send is not cancelled by closing the page: whatever was sent stays sent, so the result is always reported.
        _ = Task.Run(async () =>
        {
            ListImportResult result;
            try { result = await model.SendAsync(host, torrents); }
            catch (Exception) { result = new ListImportResult(0, [new SkippedEntry("The list", "The list", "The list could not be sent. Try again.")]); }
            activity.RunOnUiThread(() => activity.FinishImport(page, result));
        });
        UpdateSend();
        ShowSaveTo();
    }
}
