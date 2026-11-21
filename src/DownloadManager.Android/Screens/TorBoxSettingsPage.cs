using Android.Graphics;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile.TorBox;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.MaterialSwitch;

namespace Inbound.App.Screens;

/// <summary>
/// "Torrents, TorBox, archives" (t7). Only the TorBox group is here: the Account row (the key is entered in a masked sheet and never shown
/// again) and "Send magnets to TorBox first". The Torrents and Archives groups come with their own steps.
/// </summary>
internal sealed class TorBoxSettingsPage
{
    public const string Title = "Torrents, TorBox, archives";
    private readonly MainActivity _activity;
    private readonly TorBoxClient _client;
    private readonly TorBoxChoice _choice;
    private readonly TextView _status;
    private bool _hasKey, _disposed;

    public TorBoxSettingsPage(MainActivity activity, TorBoxClient client, TorBoxChoice choice)
    {
        _activity = activity;
        _client = client;
        _choice = choice;
        var dp = (int d) => Motion.Dp(activity, d);
        int Attr(int a) => Motion.Attr(activity, a);

        Root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        Root.SetBackgroundColor(new Color(Attr(Android.Resource.Attribute.ColorBackground)));
        InsetsListener.Pad(Root, top: true, bottom: true);
        var header = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(dp(8), 0, dp(16), 0);
        var back = new ImageButton(activity) { ContentDescription = "Back" };
        back.SetImageResource(Resource.Drawable.ic_arrow_back);
        back.Background = new TypedValueHolder(activity).SelectableBackground();
        header.AddView(back, new LinearLayout.LayoutParams(dp(48), dp(48)));
        var heading = new TextView(activity) { Text = Title, TextSize = 24, AccessibilityHeading = true };
        heading.SetTextColor(new Color(Attr(Resource.Attribute.colorOnSurface)));
        header.AddView(heading, new LinearLayout.LayoutParams(0, -2, 1));
        Root.AddView(header);
        back.Click += (_, _) => _activity.CloseRowPage(Root);

        var scroll = new ScrollView(activity) { FillViewport = true };
        var body = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        body.SetPadding(dp(16), dp(8), dp(16), dp(24));
        scroll.AddView(body);
        Root.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));

        var caption = new TextView(activity) { Text = "TorBox", TextSize = 14 };
        caption.SetTextColor(new Color(Attr(Resource.Attribute.colorPrimary)));
        body.AddView(caption, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(16), BottomMargin = dp(8) });
        var card = new MaterialCardView(activity) { Radius = dp(24), CardElevation = 0, StrokeWidth = 0 };
        card.SetCardBackgroundColor(new Color(Attr(Resource.Attribute.colorSurfaceContainer)));
        var rows = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        rows.SetPadding(0, dp(4), 0, dp(4));
        card.AddView(rows);
        body.AddView(card, new LinearLayout.LayoutParams(-1, -2));

        // Account: the words say whether a key is saved; Change opens the sheet.
        Row(rows, "Account", out _status, out var accountRow);
        _status.Text = "Checking…";
        var change = new MaterialButton(activity, null, Resource.Attribute.materialButtonTonalStyle) { Text = "Change", ContentDescription = "Change the TorBox account key" };
        change.SetAllCaps(false);
        accountRow.AddView(change, new LinearLayout.LayoutParams(-2, dp(44)));
        change.Click += (_, _) => TorBoxKeySheet.Show(_activity, _client, _hasKey, _activity.Capabilities.IsRemote ? _activity.HostName : "", Load);

        // Send magnets to TorBox first: kept on this device and read by Add torrent.
        Row(rows, "Send magnets to TorBox first", out var sub, out var firstRow);
        sub.Text = "Add torrent opens with TorBox chosen";
        var toggle = new MaterialSwitch(activity) { Checked = _choice.SendFirst, ContentDescription = "Send magnets to TorBox first" };
        firstRow.AddView(toggle);
        toggle.CheckedChange += (_, e) => _choice.SendFirst = e.IsChecked;
        firstRow.Clickable = true;
        firstRow.Focusable = true;
        firstRow.Background = new TypedValueHolder(activity).SelectableBackground();
        firstRow.Click += (_, _) => toggle.Toggle();
        Load();
    }

    public LinearLayout Root { get; }

    public void Dispose() => _disposed = true;

    private TextView Row(LinearLayout parent, string title, out TextView subtitle, out LinearLayout row)
    {
        var dp = (int d) => Motion.Dp(_activity, d);
        row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(dp(72));
        row.SetPadding(dp(16), dp(8), dp(16), dp(8));
        var text = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var name = new TextView(_activity) { Text = title, TextSize = 17 };
        name.SetTextColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurface)));
        subtitle = new TextView(_activity) { TextSize = 14 };
        subtitle.SetTextColor(new Color(Motion.Attr(_activity, Resource.Attribute.colorOnSurfaceVariant)));
        text.AddView(name);
        text.AddView(subtitle);
        row.AddView(text, new LinearLayout.LayoutParams(0, -2, 1));
        parent.AddView(row);
        return name;
    }

    private void Load()
    {
        TorBoxUi.Run(_activity, () => _client.AccountAsync(), result =>
        {
            if (_disposed) return;
            var account = result.Ok ? result.Accounts.FirstOrDefault() : null;
            if (account is null) { _status.Text = result.Ok ? "No TorBox account on this Host" : TorBoxText.Failure(result.Error, result.FailureKind); return; }
            _hasKey = account is { HasKey: true };
            _status.Text = TorBoxText.KeyStatus(account.HasKey, account.KeyUnreadable, !_activity.Capabilities.IsRemote, _activity.HostName);
        }, words => { if (!_disposed) _status.Text = words; });
    }
}
