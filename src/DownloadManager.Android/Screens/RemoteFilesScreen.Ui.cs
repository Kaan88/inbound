using Android.Content;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;
using Color = Android.Graphics.Color;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace Inbound.App.Screens;

/// <summary>Shared pieces of the x11 to x31 screens: banners, icon menu, copy boxes, outlined fields, pills and footers.</summary>
internal sealed partial class RemoteFilesScreen
{
    private enum BannerKind { Info, Warn, Fail }

    private int ResColor(int id) => _activity.GetColor(id);

    /// <summary>The mockup's inset surface inside sheets (key boxes, the From/To card): the page colour in dark, the icon-tile grey in light.</summary>
    private int InsetSurface() => IsNight() ? Attr(Resource.Attribute.colorSurface) : Attr(Resource.Attribute.colorSurfaceContainerHighest);

    private bool IsNight() =>
        (_activity.Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes;

    /// <summary>The mockups' field and segment outline colour (--field): a quiet grey, #4a4a4a in dark and #8a8a8a in light.</summary>
    private int FieldLine() => IsNight() ? Color.ParseColor("#4a4a4a") : Color.ParseColor("#8a8a8a");

    /// <summary>A segment of a "Sign in with" or "Encryption" choice as drawn: 44 dp, 14.5 sp medium, the check beside the label.</summary>
    private void StyleSegment(MaterialButton button)
    {
        button.SetAllCaps(false);
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 14.5f);
        button.SetTypeface(Android.Graphics.Typeface.Create("sans-serif-medium", Android.Graphics.TypefaceStyle.Normal), Android.Graphics.TypefaceStyle.Normal);
        button.StrokeColor = ColorStateList.ValueOf(new Color(FieldLine()));
        button.StrokeWidth = Dp(1);
        button.InsetTop = button.InsetBottom = 0;
        button.IconGravity = MaterialButton.IconGravityTextStart;
        button.IconPadding = Dp(8);
        button.IconSize = Dp(18);
        button.SetSingleLine(true);
    }

    private static GradientDrawable Rounded(int color, int radiusPx)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(color);
        drawable.SetCornerRadius(radiusPx);
        return drawable;
    }

    /// <summary>Info (blue), warn (amber) or fail (red) banner. The leading view defaults to the icon; an action is a text button.</summary>
    private LinearLayout Banner(BannerKind kind, string title, string? body, int icon, string? actionText = null, Action? action = null, View? leading = null)
    {
        int background, tint, text;
        switch (kind)
        {
            case BannerKind.Warn:
                background = ResColor(Resource.Color.state_waiting_container); tint = ResColor(Resource.Color.state_waiting);
                text = Attr(Resource.Attribute.colorOnSurface); break;
            case BannerKind.Fail:
                background = ResColor(Resource.Color.state_failed_container); tint = ResColor(Resource.Color.state_failed);
                text = Attr(Resource.Attribute.colorOnSurface); break;
            default:
                background = Attr(Resource.Attribute.colorPrimaryContainer); tint = Attr(Resource.Attribute.colorPrimary);
                text = Attr(Resource.Attribute.colorOnPrimaryContainer); break;
        }
        var box = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        box.Background = Rounded(background, Dp(12));
        box.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
        if (leading is null)
        {
            var glyph = new ImageView(_activity) { ImportantForAccessibility = ImportantForAccessibility.No };
            glyph.SetImageResource(icon);
            glyph.ImageTintList = ColorStateList.ValueOf(new Color(tint));
            leading = glyph;
            box.AddView(leading, new LinearLayout.LayoutParams(Dp(24), Dp(24)) { RightMargin = Dp(14), TopMargin = Dp(1) });
        }
        else box.AddView(leading, new LinearLayout.LayoutParams(-2, -2) { RightMargin = Dp(8) });
        var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var heading = new TextView(_activity) { Text = title, TextSize = body is null ? 14 : 14.5f };
        heading.SetTextColor(new Color(text));
        if (body is not null) heading.SetTypeface(Android.Graphics.Typeface.Create("sans-serif-medium", Android.Graphics.TypefaceStyle.Normal), Android.Graphics.TypefaceStyle.Normal);
        column.AddView(heading);
        if (body is not null)
        {
            var detail = new TextView(_activity) { Text = body, TextSize = 12.5f };
            detail.SetLineSpacing(Dp(3), 1f);
            detail.SetTextColor(new Color(text));
            column.AddView(detail);
        }
        if (actionText is not null && action is not null)
        {
            var button = new MaterialButton(_activity, null, Resource.Attribute.borderlessButtonStyle) { Text = actionText };
            button.SetAllCaps(false);
            button.SetPadding(0, 0, 0, 0);
            button.SetMinimumWidth(0);
            button.Click += (_, _) => action();
            column.AddView(button, new LinearLayout.LayoutParams(-2, Dp(44)) { TopMargin = Dp(6) });
        }
        box.AddView(column, new LinearLayout.LayoutParams(0, -2, 1));
        box.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(12) };
        return box;
    }

    /// <summary>The x11b menu: icon and text per row, anchored under the button.</summary>
    private void ShowIconMenu(View anchor, params (int Icon, string Text, Action Click)[] items)
    {
        var list = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        list.Background = Rounded(Attr(Resource.Attribute.colorSurfaceContainerHigh), Dp(8));
        list.SetPadding(0, Dp(8), 0, Dp(8));
        PopupWindow? popup = null;
        foreach (var (icon, label, click) in items)
        {
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(Dp(16), 0, Dp(16), 0);
            row.Background = new TypedValueHolder(_activity).SelectableBackground();
            row.AddView(SheetIcon(icon, Resource.Attribute.colorOnSurface), new LinearLayout.LayoutParams(Dp(24), Dp(24)) { RightMargin = Dp(14) });
            row.AddView(FolderText(label, 15, Resource.Attribute.colorOnSurface));
            row.ContentDescription = label;
            var action = click;
            row.Click += (_, _) => { popup?.Dismiss(); action(); };
            list.AddView(row, new LinearLayout.LayoutParams(-1, Dp(48)));
        }
        popup = new PopupWindow(list, Dp(236), -2, true) { Elevation = Dp(8) };
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        ShowMenuPopup(popup, anchor, false);
    }

    /// <summary>The mockups' menu sits 10 dp from the screen's right edge, under the anchor (or over it, as x5 draws it).</summary>
    private void ShowMenuPopup(PopupWindow popup, View anchor, bool overAnchor)
    {
        var where = new int[2];
        anchor.GetLocationOnScreen(where);
        popup.ShowAtLocation(anchor.RootView, GravityFlags.Top | GravityFlags.Right, Dp(10), where[1] + (overAnchor ? 0 : anchor.Height));
    }

    private void CopyText(string text)
    {
        try
        {
            if (_activity.GetSystemService(Context.ClipboardService) is Android.Content.ClipboardManager clipboard)
                clipboard.PrimaryClip = ClipData.NewPlainText("Inbound", text);
            _activity.ShowMessage("Copied");
        }
        catch (Exception) { /* No clipboard: nothing to say. */ }
    }

    /// <summary>A monospace box with a copy button beside it (keys, fingerprints, the rclone command).</summary>
    private View CopyBox(string text, string copyLabel, string? caption = null, string? copyText = null, bool captionInside = false)
    {
        var holder = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        string shown = text;
        if (caption is not null && captionInside) shown = caption + "\n" + text; // x20c: "SHA-256" is the box's first mono line
        else if (caption is not null)
        {
            var cap = FolderText(caption, 14, Resource.Attribute.colorOnSurfaceVariant); // x27: "Trusted before" above the box
            cap.SetPadding(Dp(2), Dp(4), 0, Dp(4));
            holder.AddView(cap);
        }
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.Top);
        var mono = new TextView(_activity) { Text = shown, TextSize = 13 };
        mono.SetLineSpacing(Dp(1), 1f);
        mono.SetTypeface(Android.Graphics.Typeface.Monospace, Android.Graphics.TypefaceStyle.Normal);
        mono.SetTextIsSelectable(true);
        mono.SetTextColor(new Color(Attr(Resource.Attribute.colorOnSurface)));
        mono.Background = Rounded(InsetSurface(), Dp(12));
        mono.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        row.AddView(mono, new LinearLayout.LayoutParams(0, -2, 1));
        var copy = IconButton(Resource.Drawable.ic_copy, copyLabel, () => CopyText(copyText ?? text), Resource.Attribute.colorOnSurfaceVariant);
        copy.SetPadding(Dp(10), Dp(10), Dp(10), Dp(10));
        row.AddView(copy, new LinearLayout.LayoutParams(Dp(40), Dp(40)) { LeftMargin = Dp(4), TopMargin = Dp(6) });
        holder.AddView(row);
        holder.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(6) };
        return holder;
    }

    /// <summary>Outlined Material field from the app's field style. <paramref name="onChange"/> sees every edit.</summary>
    private (TextInputLayout Layout, TextInputEditText Edit) OutlinedField(string label, string text, InputTypes type, bool password = false,
        Action<string>? onChange = null, string? placeholder = null)
    {
        var layout = (TextInputLayout)LayoutInflater.From(_activity)!.Inflate(Resource.Layout.remote_field, null, false)!;
        var edit = (TextInputEditText)layout.EditText!;
        layout.Hint = label;
        layout.ExpandedHintEnabled = false;
        edit.SaveEnabled = false;
        layout.SetBoxBackgroundColorStateList(ColorStateList.ValueOf(Color.Transparent));
        SetOutline(layout, FieldBox(false, false));
        layout.ErrorIconDrawable = AndroidX.AppCompat.Content.Res.AppCompatResources.GetDrawable(_activity, Resource.Drawable.ic_remote_alert);
        layout.SetErrorIconTintList(ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorError))));
        edit.FocusChange += (_, e) => SetOutline(layout, FieldBox(layout.Error is not null, e.HasFocus));
        edit.SetTextSize(Android.Util.ComplexUnitType.Sp, 15.5f);
        edit.InputType = type;
        edit.ContentDescription = label;
        if (placeholder is not null) layout.PlaceholderText = placeholder;
        if (password)
        {
            edit.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
            layout.EndIconMode = TextInputLayout.EndIconPasswordToggle;
            layout.EndIconDrawable = AndroidX.AppCompat.Content.Res.AppCompatResources.GetDrawable(_activity, Resource.Drawable.ic_remote_eye);
            layout.SetEndIconTintList(ColorStateList.ValueOf(new Color(Attr(Resource.Attribute.colorOnSurfaceVariant))));
            layout.EndIconContentDescription = "Show or hide " + label.ToLowerInvariant();
        }
        edit.Text = text;
        if (onChange is not null) edit.TextChanged += (_, _) => onChange(edit.Text ?? "");
        layout.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(8) };
        return (layout, edit);
    }

    /// <summary>The mockup's field: an 8 dp outline (1 dp, 2 dp blue when focused, 2 dp red on error) around the small label and the value.</summary>
    private Android.Graphics.Drawables.Drawable FieldBox(bool error, bool focused)
    {
        var box = new Android.Graphics.Drawables.GradientDrawable();
        box.SetColor(Color.Transparent);
        box.SetCornerRadius(Dp(8));
        box.SetStroke(error || focused ? Dp(2) : Dp(1),
            error ? new Color(Attr(Resource.Attribute.colorError)) : focused ? new Color(Attr(Resource.Attribute.colorPrimary)) : new Color(FieldLine()));
        return box;
    }

    private void SetFieldError(TextInputLayout layout, string? message)
    {
        layout.Error = message;
        layout.ErrorEnabled = message is not null;
        SetOutline(layout, FieldBox(message is not null, layout.EditText?.HasFocus == true));
    }

    private sealed class OutlineBox { public Drawable? Current; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<View, OutlineBox> Outlines = new();

    /// <summary>Draws the outline over the box only (the frame holding the label and value, not the error line below it); it takes no touches.</summary>
    private static void SetOutline(TextInputLayout layout, Drawable box)
    {
        var frame = (FrameLayout)layout.EditText!.Parent!;
        var holder = Outlines.GetValue(frame, _ =>
        {
            var created = new OutlineBox();
            frame.LayoutChange += (_, _) => created.Current?.SetBounds(0, 0, frame.Width, frame.Height);
            return created;
        });
        if (holder.Current is not null) frame.Overlay!.Remove(holder.Current);
        holder.Current = box;
        box.SetBounds(0, 0, frame.Width, frame.Height);
        frame.Overlay!.Add(box);
    }

    private View HostPill()
    {
        var pill = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        pill.SetGravity(GravityFlags.CenterVertical);
        pill.Background = Rounded(Attr(Resource.Attribute.colorPrimaryContainer), Dp(24));
        pill.SetPadding(Dp(14), 0, Dp(16), 0);
        pill.AddView(SheetIcon(Resource.Drawable.ic_host_desktop, Resource.Attribute.colorPrimary), new LinearLayout.LayoutParams(Dp(20), Dp(20)) { RightMargin = Dp(8) });
        var name = FolderText(_hostName, 16, Resource.Attribute.colorOnPrimaryContainer);
        name.SetSingleLine(true);
        pill.AddView(name);
        pill.ContentDescription = "Host " + _hostName;
        return pill;
    }

    /// <summary>The top bar of a full-screen form: back or X, title, Host pill on a paired Host.</summary>
    private View FormBar(string title, bool close, Action back)
    {
        var bar = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        var button = IconButton(close ? Resource.Drawable.ic_close : Resource.Drawable.ic_arrow_back, close ? "Close" : "Back", back);
        bar.AddView(button, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        var heading = FolderText(title, 24, Resource.Attribute.colorOnSurface);
        heading.SetSingleLine(true);
        heading.Ellipsize = TextUtils.TruncateAt.End;
        heading.SetPadding(Dp(12), 0, 0, 0);
        bar.AddView(heading, new LinearLayout.LayoutParams(0, -2, 1));
        if (!_phoneHost) bar.AddView(HostPill(), new LinearLayout.LayoutParams(-2, Dp(44)));
        bar.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(14) };
        return bar;
    }

    /// <summary>a5's footer: Cancel and the primary button on a 104 dp surface band over the scrolling form.</summary>
    private MaterialButton FormFooter(string primaryText, int primaryIcon, Action cancel, Action primary, bool enabled = true)
    {
        var band = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        band.SetGravity(GravityFlags.CenterVertical);
        band.SetBackgroundColor(new Color(Attr(Resource.Attribute.colorSurfaceContainer)));
        band.SetPadding(Dp(16), 0, Dp(16), 0);
        var cancelButton = new MaterialButton(_activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = "Cancel", CornerRadius = Dp(32) };
        cancelButton.SetAllCaps(false);
        cancelButton.Click += (_, _) => cancel();
        band.AddView(cancelButton, new LinearLayout.LayoutParams(-2, Dp(64)));
        var ok = new MaterialButton(_activity) { Text = primaryText, CornerRadius = Dp(32), Enabled = enabled };
        ok.SetAllCaps(false);
        ok.SetIconResource(primaryIcon);
        ok.IconGravity = MaterialButton.IconGravityTextStart;
        ok.Click += (_, _) => primary();
        band.AddView(ok, new LinearLayout.LayoutParams(0, Dp(64), 1) { LeftMargin = Dp(12) });
        _surface.AddView(band, new FrameLayout.LayoutParams(-1, Dp(104), GravityFlags.Bottom));
        return ok;
    }

    /// <summary>A bottom-sheet footer with Cancel and a primary pill, as in x20c, x26, x27 and x29.</summary>
    private MaterialButton SheetButtons(LinearLayout parent, string primaryText, int primaryIcon, Action? primary, Action? cancel = null)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetPadding(Dp(24), Dp(12), Dp(24), 0);
        var cancelButton = new MaterialButton(_activity, null, Resource.Attribute.materialButtonOutlinedStyle) { Text = "Cancel", CornerRadius = Dp(32) };
        cancelButton.SetAllCaps(false);
        cancelButton.Click += (_, _) => { var open = _sheet; _sheet = null; open?.Dismiss(); cancel?.Invoke(); };
        row.AddView(cancelButton, new LinearLayout.LayoutParams(-2, Dp(64)));
        var ok = new MaterialButton(_activity) { Text = primaryText, CornerRadius = Dp(32) };
        ok.SetAllCaps(false);
        if (primaryIcon != 0) { ok.SetIconResource(primaryIcon); ok.IconGravity = MaterialButton.IconGravityTextStart; }
        ok.Click += (_, _) => { var open = _sheet; _sheet = null; open?.Dismiss(); primary?.Invoke(); };
        row.AddView(ok, new LinearLayout.LayoutParams(0, Dp(64), 1) { LeftMargin = Dp(12) });
        parent.AddView(row);
        return ok;
    }

    private TextView SheetTitle(LinearLayout parent, string title, params string[] lines)
    {
        var heading = FolderText(title, 22, Resource.Attribute.colorOnSurface);
        heading.SetPadding(Dp(24), 0, Dp(24), Dp(6));
        parent.AddView(heading);
        foreach (var line in lines)
        {
            var sub = FolderText(line, 14, Resource.Attribute.colorOnSurfaceVariant);
            sub.SetLineSpacing(Dp(3), 1f);
            sub.SetPadding(Dp(24), Dp(2), Dp(24), Dp(2));
            parent.AddView(sub);
        }
        return heading;
    }

    private void AddInset(LinearLayout parent, View view)
    {
        var existing = view.LayoutParameters as LinearLayout.LayoutParams ?? new LinearLayout.LayoutParams(-1, -2);
        existing.LeftMargin = Dp(24); existing.RightMargin = Dp(24);
        if (existing.TopMargin == 0) existing.TopMargin = Dp(8);
        view.LayoutParameters = existing;
        parent.AddView(view);
    }
}
