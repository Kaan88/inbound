using System.Globalization;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Torrents;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>
/// The two sheets behind t7's "Seeding" and "Connections and limits" rows. They edit the Host's own torrent settings
/// (<see cref="SettingsDto"/>) through the same compare-and-swap write as the other settings, so a paired Host's are changed there.
/// Sheets rather than pages, because the settings page they open from is already the one open page.
/// </summary>
internal static class TorrentSettingsSheets
{
    private const double MostRatio = 100;
    private const double MostHours = 8760;
    private const long MostUploadKilobytes = 10_000_000;

    /// <summary>Keep sharing after the download, and when to stop: TorrentSeedAfterDownload, TorrentSeedRatio, TorrentSeedHours.</summary>
    // The "Wi-Fi and charging" part of the mockup's subtitle is left out: the Host has no torrent-specific network or charging setting yet (owner decision pending).
    public static void ShowSeeding(MainActivity activity, DownloadsModel downloads, QueueSettingsModel settings)
    {
        var current = downloads.Settings;
        var sheet = new Sheet(activity, "Seeding", "A finished torrent keeps uploading to other peers until it reaches the first limit below. Each torrent can have its own limits on its details.");
        var seed = sheet.Switch("Keep sharing after the download", current.TorrentSeedAfterDownload);
        var ratio = sheet.Field("Stop at ratio (0 for no ratio limit)", Text(current.TorrentSeedRatio), decimalNumber: true);
        var hours = sheet.Field("Stop after hours (0 for no time limit)", Text(current.TorrentSeedHours), decimalNumber: true);
        void Enable() => ratio.Enabled = hours.Enabled = seed.Checked;
        seed.CheckedChange += (_, _) => Enable();
        Enable();
        sheet.OnSave(() =>
        {
            if (!TryNumber(ratio, out var r) || r is < 0 or > MostRatio) return sheet.Problem("Enter a ratio from 0 to 100 (0 is no ratio limit).");
            if (!TryNumber(hours, out var h) || h is < 0 or > MostHours) return sheet.Problem("Enter hours from 0 to 8760 (0 is no time limit).");
            var on = seed.Checked;
            if (on && r == 0 && h == 0) return sheet.Problem("Seeding needs a ratio or a time limit; both can't be 0.");
            sheet.Save(settings, downloads, s => { s.TorrentSeedAfterDownload = on; s.TorrentSeedRatio = r; s.TorrentSeedHours = h; });
            return true;
        });
        sheet.Show();
    }

    /// <summary>Port, DHT, encryption and the upload limit: TorrentListenPort, TorrentDht, TorrentEncryption, TorrentUploadLimit.</summary>
    public static void ShowConnections(MainActivity activity, DownloadsModel downloads, QueueSettingsModel settings)
    {
        var current = downloads.Settings;
        var sheet = new Sheet(activity, "Connections and limits",
            "How peers reach this " + (activity.Capabilities.IsRemote ? "Host" : "phone") + ". The overall speed limit in Network still covers torrents.");
        var port = sheet.Field("Listening port (1024 to 65535)", current.TorrentListenPort > 0 ? current.TorrentListenPort.ToString(CultureInfo.InvariantCulture) : "", decimalNumber: false);
        var dht = sheet.Switch("DHT (find peers without a tracker)", current.TorrentDht);
        var encryption = sheet.Choice("Encryption", [("prefer", "Prefer (encrypted when the peer can)"), ("require", "Require (peers that can't are left out)"), ("off", "Off (plain only)")],
            current.TorrentEncryption is "require" or "off" ? current.TorrentEncryption : "prefer");
        var upload = sheet.Field("Upload limit in KB/s (empty for none)",
            current.TorrentUploadLimit > 0 ? Math.Ceiling(current.TorrentUploadLimit / (double)Fmt.BytesPerKilobyte).ToString("0", CultureInfo.InvariantCulture) : "", decimalNumber: false);
        var savedLimit = current.TorrentUploadLimit;
        var shownLimit = upload.Text ?? "";
        sheet.OnSave(() =>
        {
            var portText = (port.Text ?? "").Trim();
            var portValue = 0;
            if (portText.Length > 0 && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out portValue) || !TorrentListenPorts.IsValid(portValue)))
                return sheet.Problem("Enter a port from 1024 to 65535, or leave it empty for the Host to choose one.");
            var limitText = (upload.Text ?? "").Trim();
            long limit = 0;
            if (limitText.Length > 0)
            {
                if (!long.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes) || kilobytes > MostUploadKilobytes)
                    return sheet.Problem("Enter an upload limit up to 10,000,000 KB/s, or leave it empty.");
                // A limit that isn't a whole number of KB (set elsewhere) is kept while the box still shows what it was opened with.
                limit = limitText == shownLimit ? savedLimit : kilobytes * Fmt.BytesPerKilobyte;
            }

            var chosen = encryption();
            var on = dht.Checked;
            sheet.Save(settings, downloads, s => { s.TorrentListenPort = portValue; s.TorrentDht = on; s.TorrentEncryption = chosen; s.TorrentUploadLimit = limit; });
            return true;
        });
        sheet.Show();
    }

    private static string Text(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryNumber(TextInputEditText box, out double value)
    {
        var text = (box.Text ?? "").Trim();
        if (text.Length == 0) { value = 0; return true; }
        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private sealed class Sheet
    {
        private readonly MainActivity _activity;
        private readonly BottomSheetDialog _dialog;
        private readonly LinearLayout _root;
        private readonly TextView _error;
        private readonly MaterialButton _save, _close;
        private readonly LinearLayout _buttons;
        private Func<bool>? _validate;
        private bool _busy;

        public Sheet(MainActivity activity, string title, string about)
        {
            _activity = activity;
            var dp = (int d) => Motion.Dp(activity, d);
            _dialog = new BottomSheetDialog(activity);
            _root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
            _root.SetPadding(dp(24), dp(8), dp(24), dp(20));
            var heading = new TextView(activity) { Text = title, AccessibilityHeading = true };
            heading.SetTextAppearance(Resource.Style.TextAppearance_Material3_HeadlineSmall);
            heading.SetTextColor(Attr(Resource.Attribute.colorOnSurface));
            _root.AddView(heading);
            var blurb = new TextView(activity) { Text = about };
            blurb.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
            blurb.SetTextColor(Attr(Resource.Attribute.colorOnSurfaceVariant));
            _root.AddView(blurb, new LinearLayout.LayoutParams(-1, -2) { TopMargin = dp(8), BottomMargin = dp(8) });
            _error = new TextView(activity) { Visibility = ViewStates.Gone, AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
            _error.SetTextColor(Attr(Resource.Attribute.colorError));
            _buttons = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            _close = new MaterialButton(activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = "Cancel", CornerRadius = dp(28) };
            _buttons.AddView(_close, new LinearLayout.LayoutParams(0, dp(56), 1));
            _save = new MaterialButton(activity) { Text = "Save", CornerRadius = dp(28) };
            _buttons.AddView(_save, new LinearLayout.LayoutParams(0, dp(56), 1) { MarginStart = dp(12) });
            _close.Click += (_, _) => _dialog.Dismiss();
            _save.Click += (_, _) => { if (!_busy) _validate?.Invoke(); }; // a valid form starts its own write
        }

        private Color Attr(int a) => new(Motion.Attr(_activity, a));

        public void OnSave(Func<bool> validate) => _validate = validate;

        public bool Problem(string words)
        {
            _error.Text = words;
            _error.Visibility = ViewStates.Visible;
            return false;
        }

        public MaterialSwitch Switch(string title, bool on)
        {
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight(Motion.Dp(_activity, 56));
            var name = new TextView(_activity) { Text = title, TextSize = 17 };
            name.SetTextColor(Attr(Resource.Attribute.colorOnSurface));
            row.AddView(name, new LinearLayout.LayoutParams(0, -2, 1));
            var toggle = new MaterialSwitch(_activity) { Checked = on, ContentDescription = title };
            row.AddView(toggle);
            row.Clickable = true;
            row.Focusable = true;
            row.Click += (_, _) => { if (toggle.Enabled) toggle.Toggle(); };
            _root.AddView(row);
            return toggle;
        }

        public TextInputEditText Field(string hint, string value, bool decimalNumber)
        {
            var layout = new TextInputLayout(_activity) { Hint = hint };
            var input = new TextInputEditText(_activity)
            {
                Text = value,
                InputType = InputTypes.ClassNumber | (decimalNumber ? InputTypes.NumberFlagDecimal : 0),
            };
            input.SetSingleLine(true);
            input.ContentDescription = hint;
            layout.AddView(input);
            _root.AddView(layout, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Motion.Dp(_activity, 8) });
            return input;
        }

        public Func<string> Choice(string title, (string Value, string Words)[] options, string selected)
        {
            var label = new TextView(_activity) { Text = title };
            label.SetTextAppearance(Resource.Style.TextAppearance_Material3_BodyMedium);
            label.SetTextColor(Attr(Resource.Attribute.colorOnSurfaceVariant));
            _root.AddView(label, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Motion.Dp(_activity, 12) });
            var group = new RadioGroup(_activity) { Orientation = Orientation.Vertical };
            var buttons = new List<(RadioButton Button, string Value)>();
            foreach (var (value, words) in options)
            {
                var button = new RadioButton(_activity) { Text = words, Id = View.GenerateViewId(), Checked = value == selected };
                button.SetMinimumHeight(Motion.Dp(_activity, 48));
                group.AddView(button);
                buttons.Add((button, value));
            }

            _root.AddView(group);
            return () => buttons.FirstOrDefault(b => b.Button.Checked).Value ?? selected;
        }

        public void Save(QueueSettingsModel settings, DownloadsModel downloads, Action<SettingsDto> edit)
        {
            _busy = true;
            _save.Enabled = _close.Enabled = false;
            _error.Visibility = ViewStates.Gone;
            TorBoxUi.Run(_activity, () => settings.UpdateSettingAsync(edit), result =>
            {
                _busy = false;
                _save.Enabled = _close.Enabled = true;
                if (!result.Ok) { Problem(string.IsNullOrWhiteSpace(result.Error) ? "The Host did not accept that." : result.Error.Trim()); return; }
                if (settings.LastResult is { } latest) downloads.ApplySettingsResult(latest);
                _dialog.Dismiss();
            }, words => { _busy = false; _save.Enabled = _close.Enabled = true; Problem(words); });
        }

        public void Show()
        {
            _root.AddView(_error, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Motion.Dp(_activity, 8) });
            _root.AddView(_buttons, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Motion.Dp(_activity, 18) });
            var scroll = new ScrollView(_activity);
            scroll.AddView(_root);
            _dialog.SetContentView(scroll);
            _dialog.Show();
            _dialog.Behavior.State = BottomSheetBehavior.StateExpanded;
        }
    }
}
